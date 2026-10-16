/** `refs/heads/feature/MPT-1234-x` → `feature/MPT-1234-x`. The full ref belongs in a tooltip. */
export function shortBranch(branch: string) {
  return branch.replace(/^refs\/heads\//, '');
}

/**
 * The branch an artifact came from, prominent by design — the artifact registry exists so a feature
 * build is never mistaken for main. Trunk and release refs read as the stable spine (accent);
 * anything else is a feature branch and stays visually distinct (warning).
 */
export function BranchBadge({ branch }: { branch: string }) {
  const short = shortBranch(branch);
  const isTrunk = short === 'main' || short === 'master' || short.startsWith('release/');
  return (
    <span
      className="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-medium max-w-72 truncate"
      style={{
        backgroundColor: isTrunk ? 'var(--accent-bg)' : 'var(--warning-bg, rgba(217,119,6,0.12))',
        color: isTrunk ? 'var(--accent)' : 'var(--warning)',
      }}
      title={branch}
    >
      {short}
    </span>
  );
}
