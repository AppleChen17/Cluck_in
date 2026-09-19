"""Gmail outbound over SMTP, using only the standard library.

Same credential as the inbound half: the app password already in .env works for
smtp.gmail.com as well as imap.gmail.com. Nothing new to set up.

Three things that are easy to get wrong here:

  1. SMTP does not populate Gmail's Sent folder. That folder is written by the
     Gmail UI and by the Gmail API, not by the SMTP server. A reply sent this
     way is genuinely delivered but invisible in your own account, which during
     a demo looks exactly like a failure. The fix is to APPEND a copy over IMAP,
     which is what _append_to_sent does.
  2. The Sent folder's name is localized -- "[Gmail]/Sent Mail" in English, and
     modified UTF-7 mojibake on a Chinese account. Hardcoding the English name
     silently appends nothing there. The Sent special-use flag in the LIST
     response is the name-independent way to find it.
  3. A reply only threads in Gmail if it carries In-Reply-To and References
     pointing at the original Message-ID. Without them Gmail shows an unrelated
     new mail with a "Re:" subject, which is not what "reply" means to anyone.
"""

import email.utils
import imaplib
import logging
import smtplib
import ssl
from email.message import EmailMessage

from config import Settings
from sender.base import Sender

log = logging.getLogger("external.sender.gmail")

_SMTP_TIMEOUT = 20


def build_reply_subject(original: str | None) -> str:
    """Re: X, without stacking a second Re: on a subject that already has one."""
    subject = (original or "").strip()
    if not subject:
        return "Re:"
    if subject[:3].lower() == "re:":
        return subject
    return "Re: " + subject


def build_references(original_references: str | None, original_message_id: str | None) -> str:
    """Append the message being answered to the chain, without duplicating it.

    RFC 5322: References is the parent's References plus the parent's
    Message-ID. Clients thread on it, so a dropped or reordered chain breaks
    threading for everyone in the conversation, not just for us.
    """
    chain = (original_references or "").split()
    if original_message_id and original_message_id not in chain:
        chain.append(original_message_id)
    return " ".join(chain)


def build_email(
    *,
    from_address: str,
    from_name: str,
    to: list[str],
    subject: str,
    body: str,
    cc: list[str] | None = None,
    in_reply_to: str | None = None,
    references: str | None = None,
    message_id: str | None = None,
) -> EmailMessage:
    """Pure: an addressed, threaded, UTF-8 EmailMessage. No network."""
    message = EmailMessage()
    message["From"] = (
        email.utils.formataddr((from_name, from_address)) if from_name else from_address
    )
    message["To"] = ", ".join(to)
    if cc:
        message["Cc"] = ", ".join(cc)
    message["Subject"] = subject
    message["Date"] = email.utils.formatdate(localtime=True)
    # Our own Message-ID, so that a reply to our reply threads correctly too.
    message["Message-ID"] = message_id or email.utils.make_msgid(
        domain=from_address.partition("@")[2] or None
    )
    if in_reply_to:
        message["In-Reply-To"] = in_reply_to
    if references:
        message["References"] = references
    # set_content picks the charset itself: us-ascii for plain ASCII, utf-8 with
    # base64 transfer encoding as soon as there is a Chinese character.
    message.set_content(body)
    return message


class GmailSender(Sender):
    source = "gmail"

    def __init__(self, cfg: Settings) -> None:
        self._cfg = cfg
        self._sent_mailbox: str | None = None

    def available(self) -> bool:
        return bool(self._cfg.gmail_address and self._cfg.gmail_app_password)

    @property
    def _password(self) -> str:
        # Google displays app passwords as four groups of four and users paste
        # the spaces, exactly as the inbound adapter already handles.
        return self._cfg.gmail_app_password.replace(" ", "")

    def deliver(
        self,
        *,
        destination: str,
        body: str,
        subject: str = "",
        to: list[str] | None = None,
        cc: list[str] | None = None,
        in_reply_to: str | None = None,
        references: str | None = None,
        **_ignored,
    ) -> str:
        message = build_email(
            from_address=self._cfg.gmail_address,
            from_name=self._cfg.gmail_from_name,
            # `destination` is the primary recipient; `to` is the full list
            # when there is more than one. Dropping the rest would silently
            # deliver to fewer people than the caller asked for.
            to=to or [destination],
            subject=subject,
            body=body,
            cc=cc,
            in_reply_to=in_reply_to,
            references=references,
        )
        context = ssl.create_default_context()
        with smtplib.SMTP_SSL(
            self._cfg.gmail_smtp_host,
            self._cfg.gmail_smtp_port,
            context=context,
            timeout=_SMTP_TIMEOUT,
        ) as smtp:
            smtp.login(self._cfg.gmail_address, self._password)
            smtp.send_message(message)
        log.info("sent mail to %s (%s)", destination, message["Message-ID"])

        if self._cfg.gmail_append_to_sent:
            # Best effort, and deliberately after the send: the mail is already
            # delivered at this point, so a failure to file a copy must not be
            # reported as a failure to send.
            try:
                self._append_to_sent(message)
            except Exception as exc:  # noqa: BLE001
                log.warning("could not file a copy in the Sent folder: %s", exc)

        return str(message["Message-ID"])

    # -- filing a copy --------------------------------------------------------

    def _append_to_sent(self, message: EmailMessage) -> None:
        conn = imaplib.IMAP4_SSL(self._cfg.gmail_imap_host, self._cfg.gmail_imap_port)
        try:
            conn.login(self._cfg.gmail_address, self._password)
            mailbox = self._cfg.gmail_sent_mailbox or self._sent_mailbox
            if not mailbox:
                mailbox = self._discover_sent_mailbox(conn)
            if not mailbox:
                log.warning("no Sent mailbox found; not filing a copy")
                return
            self._sent_mailbox = mailbox
            parsed_date = email.utils.parsedate_tz(message["Date"])
            when = imaplib.Time2Internaldate(email.utils.mktime_tz(parsed_date))
            # Already read by definition: we wrote it.
            conn.append(mailbox, r"(\Seen)", when, message.as_bytes())
        finally:
            try:
                conn.logout()
            except Exception:  # noqa: BLE001
                pass

    @staticmethod
    def _discover_sent_mailbox(conn) -> str | None:
        """Find the Sent folder by its special-use flag rather than by its name."""
        typ, data = conn.list()
        if typ != "OK" or not data:
            return None
        for row in data:
            line = row.decode("utf-8", "replace") if isinstance(row, bytes) else str(row)
            if r"\Sent" not in line:
                continue
            # A LIST row is: (flags) "delimiter" "name" -- the name comes last
            # and is quoted, and on Gmail it contains both a space and a slash,
            # so splitting on whitespace would truncate it.
            quoted = line.rsplit('"', 2)
            if len(quoted) >= 2 and quoted[-2]:
                return '"{}"'.format(quoted[-2])
            return line.split()[-1]
        return None
