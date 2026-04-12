interface Props {
  approved: number;
  total: number;
  required: number;
  strategy: string;
}

export function ApprovalProgress({ approved, total, required, strategy }: Props) {
  const percentage = total > 0 ? Math.round((approved / Math.max(total, required)) * 100) : 0;
  const isMet = approved >= required;

  const strategyLabel: Record<string, string> = {
    Any: 'Any one approval needed',
    All: 'All approvals needed',
    Quorum: `${required} of ${total || required} approvals needed`,
  };

  const size = 72;
  const strokeWidth = 5;
  const radius = (size - strokeWidth) / 2;
  const circumference = 2 * Math.PI * radius;
  const offset = circumference - (percentage / 100) * circumference;

  return (
    <div className="flex items-center gap-4">
      {/* Circular progress */}
      <div className="relative shrink-0" style={{ width: size, height: size }}>
        <svg width={size} height={size} className="-rotate-90">
          <circle
            cx={size / 2}
            cy={size / 2}
            r={radius}
            fill="none"
            strokeWidth={strokeWidth}
            style={{ stroke: 'var(--border-color)' }}
          />
          <circle
            cx={size / 2}
            cy={size / 2}
            r={radius}
            fill="none"
            strokeWidth={strokeWidth}
            strokeLinecap="round"
            strokeDasharray={circumference}
            strokeDashoffset={offset}
            className="transition-all duration-500"
            style={{ stroke: isMet ? 'var(--success)' : 'var(--accent)' }}
          />
        </svg>
        <div className="absolute inset-0 flex items-center justify-center">
          <span className="text-[15px] font-bold" style={{ color: 'var(--text-primary)' }}>
            {approved}/{required}
          </span>
        </div>
      </div>

      {/* Text info */}
      <div>
        <p className="text-[13px] font-medium" style={{ color: 'var(--text-primary)' }}>
          {approved} of {required} approved
        </p>
        <p className="text-[11px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          {strategyLabel[strategy] || `Strategy: ${strategy}`}
        </p>
        {isMet && (
          <p className="text-[11px] font-medium mt-1" style={{ color: 'var(--success)' }}>
            Threshold met
          </p>
        )}
      </div>
    </div>
  );
}
