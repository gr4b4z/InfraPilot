import { ActionButton, ProblemScreen } from '@/components/system/ProblemScreen';
import { reloadApp } from '@/lib/appUpdate';
import { diagnosticsReport, RESET_FOOTNOTE } from '@/lib/diagnostics';

/**
 * Full-screen notice for when sign-in can't proceed. Retrying reloads the page unless told otherwise.
 * "Reset saved data" is offered too: a sign-in left half-finished in this browser's storage is a
 * common cause that a plain reload can't clear.
 */
export function AuthErrorScreen({
  title,
  message,
  onRetry = () => reloadApp(),
}: {
  title: string;
  message: string;
  onRetry?: () => void;
}) {
  return (
    <ProblemScreen
      title={title}
      message={<p>{message}</p>}
      details={() => diagnosticsReport(`sign-in: ${title}`, [`Message: ${message}`])}
      actions={
        <>
          <ActionButton primary onClick={onRetry}>Try again</ActionButton>
          <ActionButton onClick={() => reloadApp(true)}>Reset saved data and reload</ActionButton>
        </>
      }
      footer={RESET_FOOTNOTE}
    />
  );
}
