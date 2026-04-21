import { renderToString } from 'react-dom/server';
import App, { type AppRoute } from './App';
import { docsSections } from './content/siteContent';

const HOME_TITLE = 'InfraPilot | Self-Service Infrastructure Portal';
const HOME_DESCRIPTION =
  'InfraPilot is an open-source self-service infrastructure portal for catalog requests, approvals, deployments, promotions, and operational workflows.';

export type PrerenderRoute = {
  path: string;
  title: string;
  description: string;
  render: () => string;
};

export const routes: PrerenderRoute[] = [
  {
    path: '/',
    title: HOME_TITLE,
    description: HOME_DESCRIPTION,
    render: () => renderToString(<App initialRoute={{ kind: 'home' } satisfies AppRoute} />),
  },
  ...docsSections.map<PrerenderRoute>((section) => ({
    path: `/docs/${section.slug}/`,
    title: `${section.title} — InfraPilot docs`,
    description: section.summary,
    render: () =>
      renderToString(<App initialRoute={{ kind: 'docs', slug: section.slug } satisfies AppRoute} />),
  })),
];
