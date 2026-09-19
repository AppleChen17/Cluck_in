"""Gmail normalization, driven entirely by canned RFC 822 bytes.

No account, no network. Raw MIME lives as bytes literals in this file rather
than as .eml fixtures so each test is self-describing (and .eml is gitignored).
"""

import base64
import quopri

import pytest

from adapters.gmail_adapter import build_message


def canned(headers: str, body: bytes) -> bytes:
    return headers.strip().encode("utf-8").replace(b"\n", b"\r\n") + b"\r\n\r\n" + body


PLAIN_UTF8 = canned(
    """
From: "Alice Chen" <alice@example.com>
To: me@example.com
Subject: =?UTF-8?B?5ryU56S65riF5ZauCg==?=
Date: Fri, 19 Sep 2026 11:31:00 +0800
Message-ID: <abc123@mail.example.com>
Content-Type: text/plain; charset="utf-8"
Content-Transfer-Encoding: base64
""",
    base64.b64encode("請在彩排前確認 demo 清單。".encode("utf-8")),
)


def test_rfc2047_subject_is_decoded():
    msg = build_message(PLAIN_UTF8, uid="7", uidvalidity="42")
    assert msg.title == "演示清單"


def test_base64_chinese_body_is_decoded():
    msg = build_message(PLAIN_UTF8, uid="7", uidvalidity="42")
    assert msg.content == "請在彩排前確認 demo 清單。"


def test_display_name_wins_over_address():
    assert build_message(PLAIN_UTF8, uid="7", uidvalidity="42").sender == "Alice Chen"


def test_timestamp_keeps_its_offset():
    msg = build_message(PLAIN_UTF8, uid="7", uidvalidity="42", tz_mode="utc")
    # Same instant, expressed in UTC.
    assert msg.timestamp == "2026-09-19T03:31:00+00:00"


def test_id_prefers_message_id():
    msg = build_message(PLAIN_UTF8, uid="7", uidvalidity="42")
    assert msg.id == "gmail:abc123@mail.example.com"


def test_id_falls_back_to_uidvalidity_and_uid():
    raw = canned(
        """
From: a@example.com
Subject: No message id
Date: Fri, 19 Sep 2026 11:31:00 +0800
Content-Type: text/plain; charset="utf-8"
""",
        b"hi",
    )
    # A bare UID is only unique within one UIDVALIDITY generation.
    assert build_message(raw, uid="7", uidvalidity="42").id == "gmail:uid-42-7"


def test_blank_subject_becomes_null_not_empty_string():
    raw = canned(
        """
From: a@example.com
Subject:
Date: Fri, 19 Sep 2026 11:31:00 +0800
Content-Type: text/plain; charset="utf-8"
""",
        b"hi",
    )
    assert build_message(raw, uid="1", uidvalidity="1").title is None


def test_missing_from_still_yields_a_non_empty_sender():
    raw = canned(
        """
Subject: Orphan
Date: Fri, 19 Sep 2026 11:31:00 +0800
Content-Type: text/plain; charset="utf-8"
""",
        b"hi",
    )
    assert build_message(raw, uid="1", uidvalidity="1").sender == "(unknown sender)"


def test_bare_address_is_used_when_there_is_no_display_name():
    raw = canned(
        """
From: bob@example.com
Subject: x
Date: Fri, 19 Sep 2026 11:31:00 +0800
Content-Type: text/plain; charset="utf-8"
""",
        b"hi",
    )
    assert build_message(raw, uid="1", uidvalidity="1").sender == "bob@example.com"


MULTIPART = (
    b"From: a@example.com\r\n"
    b"Subject: Mixed\r\n"
    b"Date: Fri, 19 Sep 2026 11:31:00 +0800\r\n"
    b'Content-Type: multipart/alternative; boundary="B"\r\n\r\n'
    b"--B\r\n"
    b'Content-Type: text/plain; charset="utf-8"\r\n\r\n'
    b"plain wins\r\n"
    b"--B\r\n"
    b'Content-Type: text/html; charset="utf-8"\r\n\r\n'
    b"<p>html loses</p>\r\n"
    b"--B--\r\n"
)


def test_multipart_alternative_prefers_plain_text():
    assert build_message(MULTIPART, uid="1", uidvalidity="1").content == "plain wins"


HTML_ONLY = canned(
    """
From: a@example.com
Subject: Newsletter
Date: Fri, 19 Sep 2026 11:31:00 +0800
Content-Type: text/html; charset="utf-8"
""",
    b"<html><head><style>p{color:red}</style></head><body>"
    b"<p>Hello</p><div>World</div><script>evil()</script></body></html>",
)


def test_html_only_mail_is_flattened_and_scripts_dropped():
    content = build_message(HTML_ONLY, uid="1", uidvalidity="1").content
    assert "Hello" in content and "World" in content
    assert "evil" not in content and "color:red" not in content


def test_big5_body_is_decoded_via_fallback():
    raw = canned(
        """
From: a@example.com
Subject: Legacy
Date: Fri, 19 Sep 2026 11:31:00 +0800
Content-Type: text/plain; charset="big5"
""",
        "測試".encode("big5"),
    )
    assert build_message(raw, uid="1", uidvalidity="1").content == "測試"


def test_bogus_charset_label_does_not_raise():
    raw = canned(
        """
From: a@example.com
Subject: Bogus charset
Date: Fri, 19 Sep 2026 11:31:00 +0800
Content-Type: text/plain; charset="unicode-1-1-utf-8"
""",
        "內容".encode("utf-8"),
    )
    assert build_message(raw, uid="1", uidvalidity="1").content == "內容"


def test_quoted_printable_is_decoded():
    raw = canned(
        """
From: a@example.com
Subject: QP
Date: Fri, 19 Sep 2026 11:31:00 +0800
Content-Type: text/plain; charset="utf-8"
Content-Transfer-Encoding: quoted-printable
""",
        quopri.encodestring("確認一下".encode("utf-8")),
    )
    assert build_message(raw, uid="1", uidvalidity="1").content == "確認一下"


def test_quoted_reply_chain_is_trimmed():
    raw = canned(
        """
From: a@example.com
Subject: Re: demo
Date: Fri, 19 Sep 2026 11:31:00 +0800
Content-Type: text/plain; charset="utf-8"
""",
        b"Sounds good.\r\n\r\nOn Fri, Sep 19, 2026, Bob wrote:\r\n> the whole history\r\n",
    )
    assert build_message(raw, uid="1", uidvalidity="1").content == "Sounds good."


def test_content_is_truncated_to_the_configured_limit():
    raw = canned(
        """
From: a@example.com
Subject: Long
Date: Fri, 19 Sep 2026 11:31:00 +0800
Content-Type: text/plain; charset="utf-8"
""",
        b"x" * 5000,
    )
    msg = build_message(raw, uid="1", uidvalidity="1", max_chars=100)
    assert len(msg.content) == 101  # 100 chars plus the ellipsis


def test_malformed_date_falls_back_without_raising():
    raw = canned(
        """
From: a@example.com
Subject: Bad date
Date: not-a-date
Content-Type: text/plain; charset="utf-8"
""",
        b"hi",
    )
    msg = build_message(raw, uid="1", uidvalidity="1")
    # Never naive: the contract's date-time check rejects a missing offset.
    assert msg.timestamp[-6] in "+-" or msg.timestamp.endswith("Z")


def test_attachment_only_mail_yields_empty_content():
    raw = (
        b"From: a@example.com\r\n"
        b"Subject: File\r\n"
        b"Date: Fri, 19 Sep 2026 11:31:00 +0800\r\n"
        b'Content-Type: multipart/mixed; boundary="B"\r\n\r\n'
        b"--B\r\n"
        b"Content-Type: application/pdf\r\n"
        b"Content-Transfer-Encoding: base64\r\n"
        b'Content-Disposition: attachment; filename="a.pdf"\r\n\r\n'
        b"JVBERi0=\r\n"
        b"--B--\r\n"
    )
    # content may be empty (the schema allows it), but the message is still valid.
    assert build_message(raw, uid="1", uidvalidity="1").content == ""


@pytest.mark.parametrize(
    "raw",
    [PLAIN_UTF8, MULTIPART, HTML_ONLY],
    ids=["plain", "multipart", "html"],
)
def test_every_produced_message_validates_against_the_schema(raw, message_validator):
    message_validator.validate(build_message(raw, uid="1", uidvalidity="1").model_dump())


class TestSeenFlag:
    """`unread` must reflect reality once the UNSEEN-only filter is switched off."""

    def test_seen_flag_is_detected(self):
        from adapters.gmail_adapter import _is_seen

        assert _is_seen(rb'186 (FLAGS (\Seen) INTERNALDATE "19-Sep-2026" BODY[] {12}') is True

    def test_unseen_when_the_flag_is_absent(self):
        from adapters.gmail_adapter import _is_seen

        assert _is_seen(rb'186 (FLAGS (\Answered) INTERNALDATE "19-Sep-2026" BODY[] {12}') is False

    def test_no_flags_section_means_unseen(self):
        from adapters.gmail_adapter import _is_seen

        assert _is_seen(b'186 (INTERNALDATE "19-Sep-2026" BODY[] {12}') is False
        assert _is_seen(None) is False

    def test_unread_is_carried_into_the_message(self):
        assert build_message(PLAIN_UTF8, uid="1", uidvalidity="1", unread=False).unread is False
        assert build_message(PLAIN_UTF8, uid="1", uidvalidity="1").unread is True


class TestRealWorldQuotedReply:
    """Regression for a real Gmail reply in Traditional Chinese."""

    def test_chinese_attribution_line_is_trimmed(self):
        raw = canned(
            """
From: "Apple Chen" <apple@example.com>
Subject: =?UTF-8?B?UmU6IOWwvOixiuWwvOixig==?=
Date: Sat, 19 Sep 2026 16:31:36 +0800
Content-Type: text/plain; charset="utf-8"
""",
            "lalalalalala reply\r\n\r\nJessie Yang <j@gmail.com> "
            "於 2026年9月19日週六 下午3:59寫道：\r\n> good morning\r\n".encode("utf-8"),
        )
        assert build_message(raw, uid="186", uidvalidity="1").content == "lalalalalala reply"
