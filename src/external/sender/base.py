"""The safety gate every outbound message passes through.

Sending is the first thing this module does that leaves the machine and cannot
be undone. Reading mail wrong shows a bad summary on a dashboard; replying wrong
puts text under your name in someone else's inbox. The three guards here exist
because an AI decides what to say and a loop decides when:

  * DRY RUN, on by default. Everything is validated, routed and recorded; only
    the provider call is skipped. Nothing leaves the machine until you set
    EXTERNAL_SEND_DRY_RUN=false deliberately.
  * ALLOWLIST. When set, a real send may only target those addresses and
    channels. This is what stops an auto-reply loop from answering your
    supervisor because a model misread one sentence.
  * IDEMPOTENCY, in outbox.py. At most one reply per incoming message.

A blocked destination is an error, not a silent downgrade to a dry run. Silently
not sending while reporting success is the failure mode this codebase keeps
warning about -- the caller would believe the reply went out.
"""

import logging
from abc import ABC, abstractmethod

import normalize
from config import Settings
from schemas import SendResult
from sender.outbox import Outbox

log = logging.getLogger("external.sender")


class SendBlocked(Exception):
    """A real send was refused by the allowlist. Surfaced as HTTP 403."""


class SendUnavailable(Exception):
    """The transport is not configured. Surfaced as HTTP 503."""


class Sender(ABC):
    """One provider's outbound half."""

    source: str = "sender"

    @abstractmethod
    def deliver(self, **kwargs) -> str | None:
        """Hand the payload to the provider. Returns a provider id, if any.

        Only ever called for a real send. A dry run never reaches it, so an
        unconfigured transport costs nothing until someone means it.
        """

    @abstractmethod
    def available(self) -> bool:
        """False when credentials are missing, so /health can say so."""


class Dispatcher:
    """Applies the guards, then delegates. The only caller of Sender.deliver."""

    def __init__(self, cfg: Settings, outbox: Outbox, senders: dict[str, Sender]) -> None:
        self._cfg = cfg
        self._outbox = outbox
        self._senders = senders

    def ready(self) -> list[str]:
        """The sources that have credentials. Says nothing about the dry run."""
        return sorted(name for name, sender in self._senders.items() if sender.available())

    def sender_for(self, source: str) -> Sender:
        sender = self._senders.get(source)
        if sender is None:
            raise SendUnavailable("no sender configured for source {!r}".format(source))
        return sender

    def check_destination(self, destination: str) -> None:
        """Raise unless a real send to `destination` is permitted.

        Matching is case-insensitive and exact. An email address and a Slack
        channel ID share one list because both answer the same question: is this
        somewhere the chicken is allowed to speak?
        """
        allowed = self._cfg.send_allowlist
        if not allowed:
            return
        if destination.strip().lower() in allowed:
            return
        raise SendBlocked(
            "{!r} is not in EXTERNAL_SEND_ALLOWLIST. Add it there, or clear the "
            "list to allow every destination.".format(destination)
        )

    def dispatch(
        self,
        *,
        source: str,
        destination: str,
        body: str,
        reply_to_message_id: str | None = None,
        also_check: list[str] | None = None,
        **provider_kwargs,
    ) -> SendResult:
        """Route one outbound message and record what happened.

        `destination` is the primary target and the one the outbox shows.
        `also_check` carries every OTHER address the payload will reach -- the
        rest of a To list, and Cc. They must be checked too: an allowlist that
        only looks at the first recipient is not an allowlist, because putting
        an allowed address first would let the message reach anyone.
        """
        sender = self.sender_for(source)
        if not destination:
            raise SendUnavailable("no destination resolved for a {} send".format(source))

        dry_run = self._cfg.external_send_dry_run
        delivered = False
        provider_id = None
        error = None

        if dry_run:
            log.info("DRY RUN %s -> %s: %s", source, destination, self._outbox.preview(body))
        else:
            # Checked only for a real send: a dry run to a destination that is
            # not yet allowlisted is exactly what a rehearsal is for.
            for target in (destination, *(also_check or [])):
                self.check_destination(target)
            if not sender.available():
                raise SendUnavailable(
                    "the {} sender is not configured; check its credentials in "
                    "src/external/.env".format(source)
                )
            try:
                provider_id = sender.deliver(
                    destination=destination, body=body, **provider_kwargs
                )
                delivered = True
            except Exception as exc:  # noqa: BLE001 - reported, never raised at the client
                # Reported in the result rather than raised, so a failed reply
                # is visible on the dashboard and stays retryable (outbox.record
                # does not mark a failed send as answered).
                error = "{}: {}".format(type(exc).__name__, exc)
                log.exception("%s send to %s failed", source, destination)

        return self._outbox.record(
            SendResult(
                id=self._outbox.new_id(),
                source=source,
                target=destination,
                delivered=delivered,
                dryRun=dry_run,
                inReplyTo=reply_to_message_id,
                providerId=provider_id,
                preview=self._outbox.preview(body),
                timestamp=normalize.now_rfc3339(self._cfg.external_tz),
                error=error,
            )
        )
