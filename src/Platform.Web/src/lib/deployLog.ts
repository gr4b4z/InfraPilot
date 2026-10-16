/**
 * Classification of captured pipeline output for display. A deploy log is hundreds of lines of
 * routine chatter around a handful that explain the outcome, so the viewer's job is to make those
 * few findable: colour them, count them, and let the reader jump to the first one.
 *
 * The producer separately reports the single line it identified as the cause
 * (`DeployRun.failureReason`), which the page shows on its own. This module is the complement —
 * it marks up the log body itself, including the failures a producer never summarised.
 */

export type LogLineKind = 'error' | 'warning' | 'section' | 'normal';

/**
 * Lines that mean something went wrong. Ordered widest-net-last: the explicit pipeline commands
 * (`##[error]`, `::error`) are unambiguous, while the bare words at the end can appear in prose, so
 * they are anchored to log-line shapes (a prefix, a level field, a Kubernetes reason) rather than
 * matched anywhere in the text.
 */
const ERROR_PATTERNS: RegExp[] = [
  /##\[error\]/i,                                  // Azure DevOps / the release repo's Helm script
  /^::error\b/i,                                   // GitHub Actions workflow command
  /\blevel=(error|fatal)\b/i,                      // slog / structured output, e.g. Helm 4
  /^\s*(error|fatal|fail)\s*:/i,                   // "Error:", "fail:" — .NET and Go conventions
  /^\s*(panic|traceback)\b/i,
  /\bunhandled exception\b/i,
  /\b(CrashLoopBackOff|ErrImagePull|ImagePullBackOff|CreateContainerConfigError|InvalidImageName|OOMKilled|Evicted)\b/,
  /\bexit (code|status) [1-9]\d*\b/i,
  /^\s*STATUS:\s*failed\b/i,                       // helm release summary
  /\b(deployment|release|upgrade|rollout) failed\b/i,
];

const WARNING_PATTERNS: RegExp[] = [
  /##\[warning\]/i,
  /^::warning\b/i,
  /\blevel=warn(ing)?\b/i,
  /^\s*(warn|warning)\s*:/i,
  /^\s*Warning\s{2,}/,                             // kubectl event table: "Warning   BackOff   …"
  /^WARNING:/,
];

/** The banner lines the deploy scripts print to separate phases: `=== Pods Status ===`, `--- Logs for pod: x ---`. */
const SECTION_PATTERN = /^\s*(={2,}.*={2,}|-{2,}.*-{2,}|##\[section\].*)\s*$/;

export function classifyLogLine(line: string): LogLineKind {
  if (ERROR_PATTERNS.some((p) => p.test(line))) return 'error';
  if (WARNING_PATTERNS.some((p) => p.test(line))) return 'warning';
  if (SECTION_PATTERN.test(line)) return 'section';
  return 'normal';
}

export interface ClassifiedLine {
  /** 1-based, so it reads like an editor gutter. */
  number: number;
  text: string;
  kind: LogLineKind;
}

export interface ClassifiedLog {
  lines: ClassifiedLine[];
  errorCount: number;
  warningCount: number;
  /** 1-based line number of the first error, or null when the log is clean. */
  firstErrorLine: number | null;
}

export function classifyLog(content: string): ClassifiedLog {
  // A trailing newline is a line terminator, not an empty final line — rendering it would add a
  // phantom row to every log.
  const raw = content.replace(/\r\n/g, '\n').replace(/\n$/, '');
  const lines: ClassifiedLine[] = raw.split('\n').map((text, i) => ({
    number: i + 1,
    text,
    kind: classifyLogLine(text),
  }));

  let errorCount = 0;
  let warningCount = 0;
  let firstErrorLine: number | null = null;
  for (const line of lines) {
    if (line.kind === 'error') {
      errorCount++;
      if (firstErrorLine === null) firstErrorLine = line.number;
    } else if (line.kind === 'warning') {
      warningCount++;
    }
  }

  return { lines, errorCount, warningCount, firstErrorLine };
}

/** Byte count for a log-size hint. Binary units, because that's what a size cap is expressed in. */
export function formatLogSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(bytes < 10 * 1024 ? 1 : 0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
