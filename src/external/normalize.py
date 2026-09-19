"""Text and timestamp normalization shared by the adapters.

Everything here is pure: no network, no config, no state. That makes the whole
MIME/HTML surface testable against canned bytes with no credentials.
"""

import html
import re
import unicodedata
from datetime import datetime, timezone
from html.parser import HTMLParser

_WS_RUN = re.compile(r"[ \t\r\f\v]+")
_BLANK_RUN = re.compile(r"\n{3,}")

# Marketing mail pads its preheader with zero-width characters so the preview
# line in the inbox stays blank. Left in, it is pure noise for a reader and for
# a model, and it can be hundreds of characters long.
_ZERO_WIDTH = re.compile(r"[​‌‍⁠﻿]")
# Non-breaking and typographic spaces are whitespace, but str.strip/split do not
# treat U+00A0 as such, so it survives as a visible character.
_NBSP_LIKE = re.compile(r"[   -    　]")
# The same padding often arrives as literal entities in the text/plain
# alternative (which is machine-generated from the HTML and keeps them). Hard
# wrapping at 72 columns splits them at an arbitrary point -- real Notion mail
# contains "&nb sp;" and "&zw nj;" -- so whitespace is allowed between every
# character. Real prose never contains "&" followed by the letters of "nbsp".
_PADDING_ENTITY = re.compile(
    r"&\s*(?:"
    + "|".join(
        r"\s*".join(re.escape(ch) for ch in word)
        for word in ("zwnj", "zwj", "nbsp", "#8203", "#8204", "#8205", "#160", "#xa0")
    )
    + r")\s*;",
    re.IGNORECASE,
)

# Characters that make up ASCII-art logos and rules. Marketing mail embeds large
# blocks of these in its text/plain alternative; with a 1200 character budget
# they can crowd out every actual sentence before a model ever sees one.
_ART_CHARS = frozenset("@#*=+~^_|\\/<>:;.-,'\"`()[]{}!?$%&")
_ART_MIN_LEN = 8
_ART_RATIO = 0.6


def _is_ascii_art(line: str) -> bool:
    stripped = "".join(line.split())
    if len(stripped) < _ART_MIN_LEN:
        return False
    junk = sum(1 for ch in stripped if ch in _ART_CHARS)
    return junk / len(stripped) >= _ART_RATIO


def drop_ascii_art(text: str) -> str:
    """Remove logo art and rule lines, keeping prose, URLs and CJK untouched."""
    return "\n".join(line for line in text.splitlines() if not _is_ascii_art(line))


def _is_invisible(ch: str) -> bool:
    """True for characters that occupy no space and carry no meaning here.

    Category Cf covers the format controls (ZWNJ, ZWJ, soft hyphen, the
    bidi marks, BOM). U+034F COMBINING GRAPHEME JOINER is category Mn but is
    used purely as padding -- real Notion mail pads its preheader with it, and
    it is not printable in a legacy console, so it crashes naive output code.
    """
    return ch == "͏" or unicodedata.category(ch) == "Cf"


def strip_invisibles(text: str) -> str:
    """Remove zero-width padding and normalize exotic spaces to plain spaces."""
    if not text:
        return ""
    text = _PADDING_ENTITY.sub("", text)
    text = _ZERO_WIDTH.sub("", text)
    text = _NBSP_LIKE.sub(" ", text)
    if any(_is_invisible(ch) for ch in text):
        text = "".join(ch for ch in text if not _is_invisible(ch))
    return text

# Where a quoted reply chain starts. Trimming it keeps what we hand downstream
# short and on-topic, which matters a lot for a small local model.
_QUOTE_MARKERS = (
    re.compile(r"^\s*On .*wrote:\s*$", re.IGNORECASE),
    re.compile(r"^\s*-{2,}\s*Original Message\s*-{2,}\s*$", re.IGNORECASE),
    re.compile(r"^\s*_{10,}\s*$"),
    re.compile(r"^\s*寄件者\s*[:：]"),
    re.compile(r"^\s*發件人\s*[:：]"),
    re.compile(r"^\s*From:\s.*\bSent:\s", re.IGNORECASE),
    # Chinese Gmail's attribution line, e.g.
    #   Jessie Yang <j@gmail.com> 於 2026年9月19日週六 下午3:59寫道：
    # A line ending in 寫道: is an attribution and never prose. Seen on real mail.
    re.compile(r"^.{0,300}[寫写]道\s*[:：]\s*$"),
    # Japanese Gmail.
    re.compile(r"^.{0,300}さんは.*書きました\s*[:：]\s*$"),
    # Outlook's localized separator.
    re.compile(r"^\s*-{3,}\s*(原始郵件|原始邮件|轉寄的郵件)\s*-{3,}\s*$"),
)

_BLOCK_TAGS = {
    "br", "p", "div", "tr", "li", "h1", "h2", "h3", "h4", "h5", "h6",
    "blockquote", "section", "article", "header", "footer", "table",
}
_DROP_TAGS = {"script", "style", "head", "noscript", "svg"}


class _TextExtractor(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self._out: list[str] = []
        self._suppress = 0

    def handle_starttag(self, tag: str, attrs) -> None:
        if tag in _DROP_TAGS:
            self._suppress += 1
        elif tag in _BLOCK_TAGS:
            self._out.append("\n")

    def handle_endtag(self, tag: str) -> None:
        if tag in _DROP_TAGS and self._suppress:
            self._suppress -= 1
        elif tag in _BLOCK_TAGS:
            self._out.append("\n")

    def handle_data(self, data: str) -> None:
        if not self._suppress:
            self._out.append(data)

    def text(self) -> str:
        return "".join(self._out)


def html_to_text(markup: str) -> str:
    """Flatten HTML to readable plain text using only the stdlib."""
    if not markup:
        return ""
    parser = _TextExtractor()
    try:
        parser.feed(markup)
        parser.close()
        # convert_charrefs=True already resolved entities; unescaping again here
        # would turn a literal "&amp;" in the body into a bare "&".
        raw = parser.text()
    except Exception:
        # Malformed markup should degrade, never take the adapter down.
        raw = html.unescape(re.sub(r"<[^>]+>", " ", markup))
    return collapse_whitespace(raw)


def collapse_whitespace(text: str) -> str:
    text = strip_invisibles(text)
    text = drop_ascii_art(text)
    lines = [_WS_RUN.sub(" ", line).strip() for line in text.splitlines()]
    return _BLANK_RUN.sub("\n\n", "\n".join(lines)).strip()


def strip_quoted_reply(text: str) -> str:
    """Cut the body at the start of a quoted reply chain."""
    if not text:
        return ""
    lines = text.splitlines()
    consecutive_quotes = 0
    for i, line in enumerate(lines):
        if any(marker.match(line) for marker in _QUOTE_MARKERS):
            return "\n".join(lines[:i]).strip()
        if line.lstrip().startswith(">"):
            consecutive_quotes += 1
            if consecutive_quotes >= 3:
                return "\n".join(lines[: i - 2]).strip()
        elif line.strip():
            consecutive_quotes = 0
    return text.strip()


def truncate(text: str, limit: int) -> str:
    if limit <= 0 or len(text) <= limit:
        return text
    return text[:limit].rstrip() + "…"


def clean_label(value: str | None, fallback: str) -> str:
    """Build a sender label. The contract requires minLength 1, so never ''."""
    cleaned = collapse_whitespace(value or "")
    return cleaned or fallback


def clean_title(value: str | None) -> str | None:
    """Blank subjects must serialize as null, never as an empty string.

    `title` is ["string","null"] with minLength 1 — "" fails validation.
    """
    cleaned = collapse_whitespace(value or "")
    return cleaned or None


def to_rfc3339(dt: datetime, tz_mode: str = "local") -> str:
    """Emit an RFC 3339 timestamp with an explicit offset.

    Naive datetimes are rejected by the contract's date-time format check, so a
    missing tzinfo is treated as UTC rather than passed through.
    """
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    dt = dt.astimezone() if tz_mode == "local" else dt.astimezone(timezone.utc)
    return dt.isoformat(timespec="seconds")


def now_rfc3339(tz_mode: str = "local") -> str:
    return to_rfc3339(datetime.now(timezone.utc), tz_mode)


def parse_rfc3339(value: str) -> datetime | None:
    """Parse a contract timestamp into an instant. None means unusable.

    The inverse of to_rfc3339, with the opposite attitude to a missing offset.
    to_rfc3339 is fixing up a datetime we already own, so it assumes UTC; this
    reads someone else's input, where guessing would silently shift the value by
    the reader's offset. The contract requires an explicit offset, so a naive
    value is rejected instead.

    Callers must not compare these strings directly: the same instant is spelled
    differently depending on the offset, and lexical order is not chronological
    order across two different offsets.
    """
    if not isinstance(value, str):
        return None
    value = value.strip()
    if not value:
        return None
    # datetime.fromisoformat only accepts a bare "Z" on 3.11+; the repo README
    # claims 3.10+, and "Z" is the spelling most clients reach for first.
    if value.endswith(("Z", "z")):
        value = value[:-1] + "+00:00"
    try:
        parsed = datetime.fromisoformat(value)
    except ValueError:
        return None
    return parsed if parsed.tzinfo is not None else None


# Slack wraps links, mentions and channel refs in angle brackets. Left raw they
# read as noise to a human and to a model alike.
_SLACK_LINK = re.compile(r"<(https?://[^>|]+)\|([^>]+)>")
_SLACK_BARE_LINK = re.compile(r"<(https?://[^>]+)>")
_SLACK_CHANNEL = re.compile(r"<#(C[A-Z0-9]+)\|([^>]*)>")
_SLACK_SPECIAL = re.compile(r"<!(here|channel|everyone)>")
_SLACK_USER = re.compile(r"<@([UW][A-Z0-9]+)>")


def slack_text_to_plain(text: str, resolve_user=None) -> str:
    """Unwrap Slack's angle-bracket markup into readable text."""
    if not text:
        return ""
    out = _SLACK_LINK.sub(r"\2", text)
    out = _SLACK_BARE_LINK.sub(r"\1", out)
    out = _SLACK_CHANNEL.sub(lambda m: "#" + (m.group(2) or m.group(1)), out)
    out = _SLACK_SPECIAL.sub(lambda m: "@" + m.group(1), out)
    if resolve_user is not None:
        out = _SLACK_USER.sub(lambda m: "@" + resolve_user(m.group(1)), out)
    else:
        out = _SLACK_USER.sub(lambda m: "@" + m.group(1), out)
    # Slack escapes these three and only these three.
    out = out.replace("&lt;", "<").replace("&gt;", ">").replace("&amp;", "&")
    return collapse_whitespace(out)
