"""Gmail over IMAP, using only the standard library.

Two hard rules, both easy to get wrong and both unrecoverable if you do:

  1. NEVER mark mail as read. Two independent belts are used: EXAMINE via
     select(readonly=True), so the server refuses to set flags at all, and
     BODY.PEEK[] rather than BODY[], which would set the Seen flag itself.
  2. NEVER emit a naive timestamp or an empty sender/title. The contract
     rejects all three and a strict consumer drops the whole batch.

Verified on this machine (Python 3.13.2):
  * imaplib has no IDLE support, so polling is the only stdlib option.
  * imaplib._MAXLINE is already 1_000_000, so the old advice to raise it from
    10_000 no longer applies.
  * email.policy.default decodes RFC 2047 subjects, exposes From as an
    AddressHeader, and applies both transfer-encoding and charset in
    get_content() -- so no manual decode_header walking is needed.
"""

import email
import email.policy
import email.utils
import imaplib
import logging
import re
import ssl
import threading
import time
from datetime import datetime, timedelta, timezone

import normalize
from adapters.source_adapter import MessageSink, SourceAdapter
from config import Settings
from schemas import ExternalMessage

log = logging.getLogger("external.gmail")

_MSGID_TRIM = re.compile(r"^<|>$")
_MAX_BACKOFF = 60.0
_CHARSET_FALLBACKS = ("utf-8", "big5", "gb18030", "latin-1")


def _part_text(part) -> str:
    """Decode one MIME part, tolerating bogus charset labels."""
    try:
        content = part.get_content()
        return content if isinstance(content, str) else str(content)
    except (LookupError, UnicodeDecodeError, ValueError, AssertionError):
        payload = part.get_payload(decode=True) or b""
        declared = part.get_content_charset()
        candidates = ((declared,) if declared else ()) + _CHARSET_FALLBACKS
        for enc in candidates:
            try:
                return payload.decode(enc)
            except (LookupError, UnicodeDecodeError):
                continue
        return payload.decode("utf-8", errors="replace")


def _extract_body(msg) -> str:
    """Prefer text/plain, fall back to flattened HTML, then to a manual walk."""
    body = msg.get_body(preferencelist=("plain", "html"))
    if body is not None:
        text = _part_text(body)
        if body.get_content_type() == "text/plain":
            return text
        return normalize.html_to_text(text)

    # get_body returns None for attachment-only mail and for some malformed
    # multiparts; salvage any text part rather than emitting nothing.
    for part in msg.walk():
        ctype = part.get_content_type()
        if ctype == "text/plain":
            return _part_text(part)
        if ctype == "text/html":
            return normalize.html_to_text(_part_text(part))
    return ""


def _extract_sender(msg) -> str:
    try:
        addresses = msg["From"].addresses
        if addresses:
            first = addresses[0]
            label = first.display_name or first.addr_spec
            return normalize.clean_label(label, "(unknown sender)")
    except (AttributeError, IndexError, TypeError, ValueError):
        pass
    return normalize.clean_label(str(msg.get("From") or ""), "(unknown sender)")


def _extract_timestamp(msg, internaldate, tz_mode: str) -> str:
    try:
        parsed = email.utils.parsedate_to_datetime(msg["Date"])
        if parsed is not None:
            return normalize.to_rfc3339(parsed, tz_mode)
    except (TypeError, ValueError, KeyError):
        pass
    if internaldate:
        stamp = imaplib.Internaldate2tuple(internaldate)
        if stamp:
            moment = datetime.fromtimestamp(time.mktime(stamp), tz=timezone.utc)
            return normalize.to_rfc3339(moment, tz_mode)
    log.warning("message has no usable Date header; falling back to now")
    return normalize.now_rfc3339(tz_mode)


def _is_seen(prelude) -> bool:
    """Read the Seen flag out of a FETCH response prelude.

    The prelude looks like: b'186 (FLAGS (\\Seen) INTERNALDATE "..." BODY[] {1234}'
    """
    if not prelude:
        return False
    text = prelude.decode("utf-8", "replace") if isinstance(prelude, bytes) else str(prelude)
    match = re.search(r"FLAGS\s*\(([^)]*)\)", text, re.IGNORECASE)
    return bool(match) and "\\seen" in match.group(1).lower()


def _build_id(msg, uid: str, uidvalidity: str) -> str:
    raw = (msg.get("Message-ID") or "").strip()
    if raw:
        return "gmail:" + _MSGID_TRIM.sub("", raw)
    # A bare UID is only unique within one UIDVALIDITY generation.
    return "gmail:uid-{}-{}".format(uidvalidity, uid)


def build_message(
    raw: bytes,
    uid: str,
    uidvalidity: str,
    tz_mode: str = "local",
    max_chars: int = 2000,
    internaldate=None,
    unread: bool = True,
) -> ExternalMessage:
    """Pure: raw RFC 822 bytes in, contract-valid ExternalMessage out."""
    msg = email.message_from_bytes(raw, policy=email.policy.default)
    body = normalize.collapse_whitespace(_extract_body(msg))
    body = normalize.strip_quoted_reply(body)
    return ExternalMessage(
        id=_build_id(msg, uid, uidvalidity),
        source="gmail",
        sender=_extract_sender(msg),
        title=normalize.clean_title(str(msg.get("Subject") or "")),
        content=normalize.truncate(body, max_chars),
        timestamp=_extract_timestamp(msg, internaldate, tz_mode),
        unread=unread,
    )


class GmailAdapter(SourceAdapter):
    name = "gmail"

    def __init__(self, cfg: Settings) -> None:
        super().__init__()
        self._cfg = cfg
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def start(self, sink: MessageSink) -> None:
        if not self._cfg.gmail_address or not self._cfg.gmail_app_password:
            raise ValueError("GMAIL_ADDRESS and GMAIL_APP_PASSWORD are required")
        self._thread = threading.Thread(
            target=self._run, args=(sink,), daemon=True, name="gmail-poll"
        )
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=5)
        self._mark_connected(False)

    # -- polling loop ---------------------------------------------------------

    def _run(self, sink: MessageSink) -> None:
        backoff = 1.0
        conn = None
        uidvalidity = "0"
        while not self._stop.is_set():
            try:
                if conn is None:
                    conn, uidvalidity = self._connect()
                    self._mark_connected(True)
                    self._mark_error(None)
                    backoff = 1.0
                else:
                    # Gmail drops idle IMAP connections after roughly 29 minutes.
                    conn.noop()
                self._poll_once(conn, uidvalidity, sink)
                self._stop.wait(self._cfg.gmail_poll_seconds)
            except (imaplib.IMAP4.abort, imaplib.IMAP4.error, ssl.SSLError, OSError) as exc:
                self._mark_connected(False)
                self._mark_error("{}: {}".format(type(exc).__name__, exc))
                log.warning("gmail connection lost (%s); reconnecting in %.0fs", exc, backoff)
                conn = self._close(conn)
                self._stop.wait(backoff)
                backoff = min(backoff * 2, _MAX_BACKOFF)
            except Exception as exc:  # noqa: BLE001 - the poll thread must never die
                self._mark_error("{}: {}".format(type(exc).__name__, exc))
                log.exception("gmail poll failed")
                self._stop.wait(backoff)
                backoff = min(backoff * 2, _MAX_BACKOFF)
        self._close(conn)

    def _connect(self):
        conn = imaplib.IMAP4_SSL(self._cfg.gmail_imap_host, self._cfg.gmail_imap_port)
        # Google shows app passwords as four groups of four; users paste the spaces.
        conn.login(self._cfg.gmail_address, self._cfg.gmail_app_password.replace(" ", ""))
        # readonly=True issues EXAMINE, so the server will not set the Seen flag.
        conn.select(self._cfg.gmail_mailbox, readonly=True)
        _, data = conn.response("UIDVALIDITY")
        uidvalidity = data[0].decode() if data and data[0] else "0"
        log.info("gmail connected as %s (UIDVALIDITY=%s)", self._cfg.gmail_address, uidvalidity)
        return conn, uidvalidity

    @staticmethod
    def _close(conn):
        if conn is not None:
            try:
                conn.logout()
            except Exception:  # noqa: BLE001
                pass
        return None

    def _poll_once(self, conn, uidvalidity: str, sink: MessageSink) -> None:
        since = datetime.now(timezone.utc) - timedelta(days=self._cfg.gmail_since_days)
        # IMAP SINCE is date-granular and evaluated against server-side
        # INTERNALDATE, so it is only a coarse pre-filter.
        criteria = ["SINCE", since.strftime("%d-%b-%Y")]
        if self._cfg.gmail_only_unseen:
            criteria.insert(0, "UNSEEN")
        typ, data = conn.uid("SEARCH", None, *criteria)
        if typ != "OK" or not data or not data[0]:
            return
        # UIDs, not sequence numbers: sequence numbers renumber on expunge.
        for uid in data[0].split()[-self._cfg.gmail_max_fetch :]:
            if self._stop.is_set():
                return
            self._fetch_one(conn, uid, uidvalidity, sink)

    def _fetch_one(self, conn, uid: bytes, uidvalidity: str, sink: MessageSink) -> None:
        # BODY.PEEK[] -- BODY[] would set the Seen flag on the user's real mailbox.
        typ, data = conn.uid("FETCH", uid, "(FLAGS BODY.PEEK[] INTERNALDATE)")
        if typ != "OK" or not data:
            return
        for item in data:
            if not isinstance(item, tuple) or len(item) < 2:
                continue
            try:
                message = build_message(
                    raw=item[1],
                    uid=uid.decode(),
                    uidvalidity=uidvalidity,
                    tz_mode=self._cfg.external_tz,
                    max_chars=self._cfg.external_max_content_chars,
                    internaldate=item[0],
                    # The prelude carries FLAGS; report the real state rather
                    # than assuming unread, which is wrong once the UNSEEN
                    # filter is switched off.
                    unread=not _is_seen(item[0]),
                )
            except Exception as exc:  # noqa: BLE001 - one bad mail must not stop the poll
                self._mark_error("parse uid {!r}: {}: {}".format(uid, type(exc).__name__, exc))
                log.exception("failed to normalize message uid=%r", uid)
                continue
            if sink(message):
                self._mark_emitted(message.timestamp)
