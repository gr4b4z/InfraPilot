/** Full-screen notice for when sign-in can't proceed. Retrying reloads the page unless told otherwise. */
export function AuthErrorScreen({
  title,
  message,
  onRetry = () => window.location.reload(),
}: {
  title: string;
  message: string;
  onRetry?: () => void;
}) {
  return (
    <div className="flex flex-col items-center justify-center h-screen gap-4" style={{ backgroundColor: 'var(--bg-primary)' }}>
      <p className="text-[14px] font-medium" style={{ color: 'var(--danger)' }}>
        {title}
      </p>
      <p className="text-[13px] max-w-md text-center" style={{ color: 'var(--text-muted)' }}>
        {message}
      </p>
      <button
        onClick={onRetry}
        className="px-4 py-2 text-[13px] font-medium rounded-lg text-white"
        style={{ backgroundColor: 'var(--accent)' }}
      >
        Try Again
      </button>
    </div>
  );
}
