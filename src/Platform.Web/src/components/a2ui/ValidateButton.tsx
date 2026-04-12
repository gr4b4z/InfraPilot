interface Props {
  onValidate: () => void;
}

export function ValidateButton({ onValidate }: Props) {
  return (
    <button
      type="button"
      onClick={onValidate}
      className="w-full py-2.5 text-sm font-semibold rounded-lg transition-colors text-white"
      style={{ backgroundColor: 'var(--accent)' }}
    >
      Validate
    </button>
  );
}
