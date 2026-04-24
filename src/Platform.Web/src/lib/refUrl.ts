import type { DeployReference } from '@/lib/types';

type RepoRefLike = Pick<DeployReference, 'type' | 'url' | 'provider' | 'revision'>;

// Build a deep link to the specific commit when the reference is a repository
// with a known provider and a revision. Falls back to the bare repo URL.
export function resolveReferenceHref(ref: RepoRefLike): string | undefined {
  if (ref.type !== 'repository' || !ref.url || !ref.revision) return ref.url ?? undefined;
  const repo = ref.url.replace(/\.git$/, '').replace(/\/$/, '');
  const rev = ref.revision;
  switch (ref.provider) {
    case 'github':
    case 'azure-devops':
      return `${repo}/commit/${rev}`;
    case 'gitlab':
      return `${repo}/-/commit/${rev}`;
    case 'bitbucket':
      return `${repo}/commits/${rev}`;
    default:
      return ref.url;
  }
}
