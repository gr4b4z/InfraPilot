import { Fragment, useMemo, type ReactNode } from 'react';

/**
 * Turns URLs inside plain text into links.
 *
 * <p>For text that arrives from outside the platform — a ticket description copied out of Jira, a
 * comment someone pasted a build log URL into. It is rendered as text and stays text: the tokeniser
 * walks the string and emits React elements, so there is no HTML parsing anywhere and no
 * <code>dangerouslySetInnerHTML</code>. Markup in the source shows up as the characters that were
 * typed, which is the point — this makes links clickable, it does not make the field render
 * markup.</p>
 *
 * <p>Only <code>http</code> and <code>https</code> (and bare <code>www.</code>, which gets an
 * https prefix) are recognised. That exclusion is the security boundary: a
 * <code>javascript:</code> or <code>data:</code> URI in someone's comment can never become an
 * anchor, because the pattern cannot match it in the first place.</p>
 */

// Deliberately narrow. Anything not matched here renders as ordinary text, which is the safe
// direction to fail in.
const URL_PATTERN = /(?:https?:\/\/|www\.)[^\s<>"'`]+/gi;

const BRACKET_PAIRS: readonly (readonly [string, string])[] = [
  ['(', ')'],
  ['[', ']'],
  ['{', '}'],
];

function occurrences(text: string, ch: string): number {
  let n = 0;
  for (const c of text) if (c === ch) n++;
  return n;
}

/**
 * Trims characters that the pattern greedily swallowed but which belong to the sentence rather than
 * the URL.
 *
 * <p>Closing brackets are the interesting case: in "(see https://x/a)" the bracket closes the
 * aside, while in "https://x/wiki/Foo_(bar)" it is part of the address. Counting decides — a
 * closing bracket is dropped only when the URL has no opener to match it.</p>
 */
function trimTrailingPunctuation(raw: string): string {
  let url = raw;
  let changed = true;

  while (changed && url.length > 0) {
    changed = false;

    while (url.length > 0 && '.,;:!?'.includes(url[url.length - 1])) {
      url = url.slice(0, -1);
      changed = true;
    }

    for (const [open, close] of BRACKET_PAIRS) {
      while (url.endsWith(close) && occurrences(url, close) > occurrences(url, open)) {
        url = url.slice(0, -1);
        changed = true;
      }
    }
  }

  return url;
}

interface Token {
  text: string;
  href?: string;
}

function tokenize(text: string): Token[] {
  const tokens: Token[] = [];
  let cursor = 0;

  for (const match of text.matchAll(URL_PATTERN)) {
    const start = match.index ?? 0;
    const url = trimTrailingPunctuation(match[0]);

    // Everything trimmed away was punctuation, not a link — leave it in the surrounding text.
    if (url.length === 0) continue;

    if (start > cursor) tokens.push({ text: text.slice(cursor, start) });
    tokens.push({ text: url, href: url.toLowerCase().startsWith('www.') ? `https://${url}` : url });
    cursor = start + url.length;
  }

  if (cursor < text.length) tokens.push({ text: text.slice(cursor) });
  return tokens;
}

/**
 * Renders <paramref name="text"/> with its URLs as links. Whitespace is emitted verbatim, so a
 * parent with `whitespace-pre-wrap` keeps its line breaks.
 */
export function Linkified({ text }: { text: string }): ReactNode {
  const tokens = useMemo(() => tokenize(text), [text]);

  return (
    <>
      {tokens.map((token, i) => (
        <Fragment key={i}>
          {token.href ? (
            <a
              href={token.href}
              target="_blank"
              rel="noopener noreferrer"
              // These bodies sit inside cards that are sometimes themselves clickable; following a
              // link should never also trigger the row behind it.
              onClick={(e) => e.stopPropagation()}
              className="underline underline-offset-2 transition-opacity hover:opacity-80"
              style={{ color: 'var(--accent)' }}
            >
              {token.text}
            </a>
          ) : (
            token.text
          )}
        </Fragment>
      ))}
    </>
  );
}
