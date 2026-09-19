"""Normalization edge cases found against a real mailbox."""

import normalize as n


class TestInvisiblePadding:
    """Marketing mail pads its preheader so the inbox preview line stays blank."""

    def test_real_zero_width_characters_are_removed(self):
        assert n.collapse_whitespace("Hi‌​‍there﻿") == "Hithere"

    def test_nbsp_becomes_a_plain_space(self):
        assert n.collapse_whitespace("a  b") == "a b"

    def test_literal_entities_are_removed(self):
        assert n.collapse_whitespace("Join us &nbsp; &zwnj; today") == "Join us today"

    def test_entities_split_by_hard_wrapping_are_removed(self):
        """Real Notion mail wraps at 72 columns mid-entity: "&nb sp;"."""
        assert n.collapse_whitespace("Join us &nb sp; &zw nj; today") == "Join us today"

    def test_entity_split_across_a_newline_is_removed(self):
        assert n.collapse_whitespace("Join us &\nzwnj; today") == "Join us today"

    def test_an_ampersand_in_prose_is_untouched(self):
        assert n.collapse_whitespace("Tom & Jerry") == "Tom & Jerry"


class TestAsciiArt:
    """Logo art in a text/plain alternative can crowd out every real sentence."""

    def test_logo_art_lines_are_dropped(self):
        art = "hello\n:=-. :#@@@@@@#*=- :#@@@@@@@@@@@@+\nworld"
        assert n.collapse_whitespace(art) == "hello\nworld"

    def test_horizontal_rules_are_dropped(self):
        assert n.collapse_whitespace("above\n--------------------\nbelow") == "above\nbelow"

    def test_urls_survive(self):
        url = "https://notion.so/?target=external&utm_campaign=Whats+New+3.6"
        assert url in n.collapse_whitespace("see " + url)

    def test_angle_wrapped_urls_survive(self):
        line = "<https://accounts.google.com/AccountChooser?Email=a@b.com>"
        assert n.collapse_whitespace(line) == line

    def test_chinese_prose_survives(self):
        text = "已啟用兩步驟驗證功能，請確認。"
        assert n.collapse_whitespace(text) == text

    def test_markdown_bullets_survive(self):
        assert n.collapse_whitespace("- review the demo checklist") == "- review the demo checklist"

    def test_short_punctuation_lines_survive(self):
        assert n.collapse_whitespace("a -> b") == "a -> b"


class TestQuotedReply:
    def test_english_marker(self):
        assert n.strip_quoted_reply("Reply.\nOn Fri, Bob wrote:\n> old") == "Reply."

    def test_chinese_marker(self):
        assert n.strip_quoted_reply("回覆。\n寄件者: Bob\n舊內容") == "回覆。"

    def test_run_of_quoted_lines(self):
        assert n.strip_quoted_reply("Reply.\n\n> a\n> b\n> c") == "Reply."

    def test_text_without_a_quote_is_unchanged(self):
        assert n.strip_quoted_reply("Just a message.") == "Just a message."


class TestTitleAndSender:
    def test_blank_title_becomes_none(self):
        assert n.clean_title("   ") is None
        assert n.clean_title(None) is None

    def test_sender_never_empty(self):
        assert n.clean_label("", "(unknown sender)") == "(unknown sender)"
        assert n.clean_label("    ", "(unknown sender)") == "(unknown sender)"


class TestSlackMarkup:
    def test_named_link_keeps_the_label(self):
        assert n.slack_text_to_plain("<https://x.com|the build>") == "the build"

    def test_bare_link_keeps_the_url(self):
        assert n.slack_text_to_plain("<https://a.b/c>") == "https://a.b/c"

    def test_channel_reference_uses_the_name(self):
        assert n.slack_text_to_plain("<#C99|ci>") == "#ci"

    def test_slack_escapes_are_undone(self):
        assert n.slack_text_to_plain("5 &lt; 7 &amp;&amp; 9 &gt; 2") == "5 < 7 && 9 > 2"


class TestLocalizedQuoteMarkers:
    """Found on a real Gmail reply: the attribution line is localized.

    Left in, it names a person who has nothing to do with the new message, which
    is exactly the kind of noise that misleads a relevance classifier.
    """

    def test_traditional_chinese_gmail_attribution(self):
        body = (
            "lalalalalala reply\n\n"
            "Jessie Yang <j@gmail.com> 於 2026年9月19日週六 下午3:59寫道：\n"
            "> good morning"
        )
        assert n.strip_quoted_reply(n.collapse_whitespace(body)) == "lalalalalala reply"

    def test_simplified_chinese_attribution(self):
        assert n.strip_quoted_reply("回覆內容\nBob 于 2026年9月19日 写道:\n> old") == "回覆內容"

    def test_outlook_localized_separator(self):
        assert n.strip_quoted_reply("回覆\n------ 原始郵件 ------\n舊內容") == "回覆"

    def test_japanese_attribution(self):
        body = "返信です\n2026年9月19日 Bob さんは書きました：\n> old"
        assert n.strip_quoted_reply(body) == "返信です"

    def test_prose_containing_the_same_characters_survives(self):
        """A line only counts as an attribution when it ENDS with 寫道: ."""
        assert n.strip_quoted_reply("他寫道歉信給我了") == "他寫道歉信給我了"

    def test_prose_mentioning_writing_survives(self):
        text = "報告我寫道一半就卡住了，晚點繼續"
        assert n.strip_quoted_reply(text) == text


class TestInvisibleFormatCharacters:
    """Real Notion mail pads with U+034F, which also crashes legacy consoles."""

    def test_combining_grapheme_joiner_is_removed(self):
        assert n.collapse_whitespace("Kai͏ Hsuan͏ Tsao") == "Kai Hsuan Tsao"

    def test_soft_hyphen_is_removed(self):
        assert n.collapse_whitespace("demo­nstration") == "demonstration"

    def test_bidi_marks_are_removed(self):
        assert n.collapse_whitespace("‎Hello‏") == "Hello"

    def test_output_is_console_safe(self):
        """Everything surviving must encode in a legacy code page or be CJK."""
        cleaned = n.collapse_whitespace("Kai͏ Hsuan‌ Tsao­")
        assert all(ord(ch) > 0x2E80 or ch.isprintable() for ch in cleaned)

    def test_real_cjk_survives(self):
        assert n.collapse_whitespace("已啟用兩步驟驗證功能") == "已啟用兩步驟驗證功能"

    def test_emoji_survives(self):
        assert n.collapse_whitespace("build failed 🔥") == "build failed 🔥"
