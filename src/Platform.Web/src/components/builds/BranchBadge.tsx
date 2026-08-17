/**
 * The branch a build came from, prominent by design — the build registry exists so a feature build
 * is never mistaken for main. Trunk and release refs read as the stable spine (accent); anything
 * else is a feature branch and stays visually distinct (warning).
 */
export function BranchBadge({ branch }: { branch: string }) {
  const short = branch.replace(/^refs\/heads\//, '');
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
