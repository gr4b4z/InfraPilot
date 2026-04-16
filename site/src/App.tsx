import { useEffect, useMemo, useState } from 'react';
import {
  apiDocs,
  docsSections,
  docsGroupOrder,
  evaluatorChecklist,
  exampleCards,
  faq,
  featureCards,
  heroHighlights,
  imageUrl,
  integrations,
  navItems,
  proofMetrics,
  proofPanels,
  repositoryUrl,
  useCaseCards,
  type TableColumn,
  type TableRow,
  workflowSteps,
} from './content/siteContent';

type AppRoute =
  | { kind: 'home' }
  | { kind: 'docs'; slug: string };

function readRoute(): AppRoute {
  const hash = window.location.hash || '';

  if (hash.startsWith('#/docs/')) {
    return { kind: 'docs', slug: hash.replace('#/docs/', '') || 'introduction' };
  }

  if (hash === '#/docs') {
    return { kind: 'docs', slug: 'introduction' };
  }

  return { kind: 'home' };
}

function groupedDocs() {
  const grouped = docsSections.reduce<Record<string, typeof docsSections>>((acc, section) => {
    acc[section.group] ??= [];
    acc[section.group].push(section);
    return acc;
  }, {});

  return docsGroupOrder.reduce<Record<string, typeof docsSections>>((acc, group) => {
    if (grouped[group]) {
      acc[group] = grouped[group];
    }
    return acc;
  }, {});
}

function App() {
  const [route, setRoute] = useState<AppRoute>(() =>
    typeof window === 'undefined' ? { kind: 'home' } : readRoute(),
  );

  useEffect(() => {
    const handleHashChange = () => setRoute(readRoute());
    window.addEventListener('hashchange', handleHashChange);
    return () => window.removeEventListener('hashchange', handleHashChange);
  }, []);

  const docsByGroup = useMemo(() => groupedDocs(), []);

  if (route.kind === 'docs') {
    const currentSection = docsSections.find((section) => section.slug === route.slug) ?? docsSections[0];
    const currentIndex = docsSections.findIndex((section) => section.slug === currentSection.slug);
    const previousSection = currentIndex > 0 ? docsSections[currentIndex - 1] : null;
    const nextSection = currentIndex < docsSections.length - 1 ? docsSections[currentIndex + 1] : null;
    const currentGroupSections = docsSections.filter((section) => section.group === currentSection.group);
    const currentGroupIndex = currentGroupSections.findIndex((section) => section.slug === currentSection.slug);
    const previousInGroup = currentGroupIndex > 0 ? currentGroupSections[currentGroupIndex - 1] : null;
    const nextInGroup =
      currentGroupIndex < currentGroupSections.length - 1 ? currentGroupSections[currentGroupIndex + 1] : null;

    return (
      <div className="site-shell">
        <Header />
        <main className="docs-page">
          <aside className="docs-sidebar" aria-label="Documentation navigation">
            <div className="docs-sidebar-panel">
              <p className="eyebrow">Documentation</p>
              <h1>InfraPilot docs</h1>
              <p className="docs-sidebar-copy">
                Product docs, quick start, integration setup, API setup, and operational guidance in one place.
              </p>
            </div>

            <nav className="docs-groups">
              {Object.entries(docsByGroup).map(([group, sections]) => (
                <div className="docs-group" key={group}>
                  <p>{group}</p>
                  {sections.map((section) => (
                    <a
                      key={section.slug}
                      className={section.slug === currentSection.slug ? 'docs-link active' : 'docs-link'}
                      href={`#/docs/${section.slug}`}
                    >
                      {section.title}
                    </a>
                  ))}
                </div>
              ))}
            </nav>
          </aside>

          <section className="docs-content">
            <div className="docs-breadcrumbs">
              <a href="#home">Home</a>
              <span>/</span>
              <span>{currentSection.group}</span>
              <span>/</span>
              <span>{currentSection.title}</span>
            </div>

            <article className="docs-article">
              <p className="eyebrow">{currentSection.group}</p>
              <h2>{currentSection.title}</h2>
              <p className="docs-summary">{currentSection.summary}</p>

              {currentSection.paragraphs.map((paragraph) => (
                <p key={paragraph}>{paragraph}</p>
              ))}

              {currentSection.bullets ? (
                <ul>
                  {currentSection.bullets.map((bullet) => (
                    <li key={bullet}>{bullet}</li>
                  ))}
                </ul>
              ) : null}

              {currentSection.code ? (
                <div className="docs-code-block">
                  <pre>
                    <code>{currentSection.code}</code>
                  </pre>
                </div>
              ) : null}

              {currentSection.note ? <div className="docs-note">{currentSection.note}</div> : null}

              {apiDocs[currentSection.slug] ? (
                <div className="docs-api-reference">
                  {apiDocs[currentSection.slug].map((block) => (
                    <section className="docs-reference-block" key={block.title}>
                      <h3>{block.title}</h3>
                      {block.description ? <p>{block.description}</p> : null}
                      {block.columns && block.rows ? <DataTable columns={block.columns} rows={block.rows} /> : null}
                      {block.code ? (
                        <pre>
                          <code>{block.code}</code>
                        </pre>
                      ) : null}
                    </section>
                  ))}
                </div>
              ) : null}
            </article>

            <div className="docs-pagination">
              <div className="docs-pagination-row">
                {previousInGroup ? (
                  <a className="docs-pagination-link" href={`#/docs/${previousInGroup.slug}`}>
                    <span>Previous in {currentSection.group}</span>
                    <strong>{previousInGroup.title}</strong>
                  </a>
                ) : (
                  <span className="docs-pagination-placeholder" />
                )}

                {nextInGroup ? (
                  <a className="docs-pagination-link align-right" href={`#/docs/${nextInGroup.slug}`}>
                    <span>Next in {currentSection.group}</span>
                    <strong>{nextInGroup.title}</strong>
                  </a>
                ) : (
                  <span className="docs-pagination-placeholder" />
                )}
              </div>

              {(previousSection || nextSection) ? (
                <div className="docs-pagination-meta">
                  {previousSection ? (
                    <a href={`#/docs/${previousSection.slug}`}>
                      Global previous: <strong>{previousSection.title}</strong>
                    </a>
                  ) : <span />}
                  {nextSection ? (
                    <a className="align-right" href={`#/docs/${nextSection.slug}`}>
                      Global next: <strong>{nextSection.title}</strong>
                    </a>
                  ) : <span />}
                </div>
              ) : null}
            </div>
          </section>
        </main>
      </div>
    );
  }

  return (
    <div className="site-shell">
      <Header />

      <main id="home">
        <section className="hero-section">
          <div className="hero-copy">
            <p className="eyebrow">Open-source infrastructure portal</p>
            <h1>Bring self-service, approvals, deployments, and promotions into one platform workflow.</h1>
            <p className="hero-lede">
              InfraPilot gives platform teams a governed front door for common operational work. Publish request
              patterns as YAML, route them through approvals, execute them through your existing toolchain, and keep
              deployment and promotion state visible in one place.
            </p>

            <div className="hero-actions">
              <a className="button button-primary" href="#/docs/introduction">
                Read docs
              </a>
              <a className="button button-secondary" href="#/docs/quick-start">
                Quick start
              </a>
              <a className="button button-secondary" href="#use-cases">
                Explore use cases
              </a>
            </div>

            <div className="hero-links">
              <a className="text-link" href={repositoryUrl} target="_blank" rel="noreferrer">
                View on GitHub
              </a>
              <a className="text-link" href={imageUrl} target="_blank" rel="noreferrer">
                Container image
              </a>
              <a className="text-link" href="#/docs/evaluation-guide">
                Evaluation guide
              </a>
            </div>

            <ul className="hero-highlights">
              {heroHighlights.map((highlight) => (
                <li key={highlight}>{highlight}</li>
              ))}
            </ul>
          </div>

          <div className="hero-visual">
            <div className="hero-card">
              <img src="./assets/hero.png" alt="InfraPilot product illustration" />
              <div className="hero-card-copy">
                <span>Platform teams first</span>
                <h2>Governed self-service without rebuilding your stack</h2>
                <p>
                  Standardize requests, approvals, deployments, promotions, and operational controls across the
                  platform lifecycle.
                </p>
              </div>
            </div>
          </div>
        </section>

        <section className="metrics-section" aria-label="Proof points">
          {proofMetrics.map((metric) => (
            <article className="metric-card" key={metric.label}>
              <strong>{metric.value}</strong>
              <h2>{metric.label}</h2>
              <p>{metric.detail}</p>
            </article>
          ))}
        </section>

        <section className="content-section" id="proof">
          <div className="section-heading">
            <p className="eyebrow">Product Proof</p>
            <h2>What the product experience actually looks like</h2>
            <p>
              InfraPilot is more than a concept page. These flat previews mirror the actual product surfaces the repo
              already implements: catalog discovery, approval review, and deployment-plus-promotion visibility.
            </p>
          </div>

          <div className="proof-grid">
            {proofPanels.map((panel) => (
              <article className="proof-card" key={panel.title}>
                <div className="proof-card-top">
                  <span>{panel.label}</span>
                  <strong>{panel.eyebrow}</strong>
                </div>
                <h3>{panel.title}</h3>
                <div className="proof-surface">
                  <div className="proof-toolbar">
                    <span />
                    <span />
                    <span />
                  </div>
                  <div className="proof-lines">
                    {panel.lines.map((line) => (
                      <div className="proof-line" key={line}>
                        <strong>{line}</strong>
                        <small>active</small>
                      </div>
                    ))}
                  </div>
                </div>
              </article>
            ))}
          </div>
        </section>

        <section className="content-section" id="product">
          <div className="section-heading">
            <p className="eyebrow">Product</p>
            <h2>A portal surface that matches the way platform teams actually work</h2>
            <p>
              InfraPilot already exposes catalog requests, approvals, deployments, promotions, webhooks, and admin
              controls in the application. The public site now keeps the story focused while the docs live separately.
            </p>
          </div>

          <div className="card-grid">
            {featureCards.map((card) => (
              <article className="feature-card" key={card.title}>
                <h3>{card.title}</h3>
                <p>{card.body}</p>
                <ul>
                  {card.points.map((point) => (
                    <li key={point}>{point}</li>
                  ))}
                </ul>
              </article>
            ))}
          </div>
        </section>

        <section className="content-section" id="workflows">
          <div className="section-heading">
            <p className="eyebrow">How It Works</p>
            <h2>From request definition to deployment signal</h2>
            <p>
              The best way to evaluate InfraPilot is to see how the pieces connect, from YAML-defined forms through
              approvals, execution, and deployment reporting.
            </p>
          </div>

          <div className="timeline">
            {workflowSteps.map((step) => (
              <article className="timeline-step" key={step.title}>
                <h3>{step.title}</h3>
                <p>{step.body}</p>
              </article>
            ))}
          </div>
        </section>

        <section className="content-section" id="docs">
          <div className="section-heading">
            <p className="eyebrow">Docs</p>
            <h2>A dedicated documentation hub, not homepage cards</h2>
            <p>
              The docs now live on their own route-style view with grouped sections for introduction, quick start,
              evaluation, configuration, integration setup, catalog authoring, API setup, use cases, and operations.
            </p>
          </div>

          <article className="docs-teaser">
            <div>
              <h3>Browse docs like a product manual</h3>
              <p>
                Navigate documentation by section from a sidebar, keep the active article centered, and move between
                pages with previous and next controls.
              </p>
            </div>
            <div className="docs-teaser-actions">
              <a className="button button-primary" href="#/docs/introduction">
                Open docs
              </a>
              <a className="button button-secondary" href="#/docs/integration-setup">
                Integration setup
              </a>
            </div>
          </article>
        </section>

        <section className="content-section" id="use-cases">
          <div className="section-heading">
            <p className="eyebrow">Use Cases</p>
            <h2>Start from the workflow you want to improve</h2>
            <p>
              The APIs explain how InfraPilot works, but adoption usually starts with a concrete operational problem.
              These use cases describe where the product fits best and how the existing catalog examples map to real
              team workflows.
            </p>
          </div>

          <div className="use-case-grid">
            {useCaseCards.map((useCase) => (
              <article className="use-case-card" key={useCase.title}>
                <span>{useCase.audience}</span>
                <h3>{useCase.title}</h3>
                <p>{useCase.summary}</p>
                <a className="text-link" href={useCase.href}>
                  Read use case
                </a>
              </article>
            ))}
          </div>
        </section>

        <section className="content-section" id="examples">
          <div className="section-heading">
            <p className="eyebrow">Examples</p>
            <h2>Real request patterns from the repository</h2>
            <p>
              InfraPilot ships with concrete catalog examples instead of placeholder documentation. These snippets show
              how platform teams can shape forms, approvals, and executors for common workflows.
            </p>
          </div>

          <div className="example-grid">
            {exampleCards.map((example) => (
              <article className="example-card" key={example.title}>
                <div className="example-meta">
                  <span>{example.category}</span>
                  <h3>{example.title}</h3>
                </div>
                <p>{example.description}</p>
                <pre><code>{example.snippet}</code></pre>
              </article>
            ))}
          </div>
        </section>

        <section className="content-section" id="integrations">
          <div className="section-heading">
            <p className="eyebrow">Integrations</p>
            <h2>Works with the systems platform teams already run</h2>
            <p>
              InfraPilot adds a governance and self-service layer in front of your delivery stack. It is designed to
              integrate, not replace.
            </p>
          </div>

          <div className="integration-grid">
            {integrations.map((integration) => (
              <article className="integration-card" key={integration.name}>
                <h3>{integration.name}</h3>
                <p>{integration.description}</p>
              </article>
            ))}
          </div>
        </section>

        <section className="content-section">
          <div className="section-heading">
            <p className="eyebrow">First 30 Minutes</p>
            <h2>A practical evaluation checklist</h2>
            <p>
              This is the shortest realistic path for evaluating InfraPilot. Each step includes what to do and what you
              should expect to see if the platform is wired correctly.
            </p>
          </div>

          <div className="checklist-grid">
            {evaluatorChecklist.map((item) => (
              <article className="checklist-card" key={item.title}>
                <h3>{item.title}</h3>
                <p><strong>Do this:</strong> {item.action}</p>
                <p><strong>Expect:</strong> {item.expected}</p>
              </article>
            ))}
          </div>

          <div className="faq-grid">
            {faq.map((item) => (
              <article className="faq-card" key={item.question}>
                <h3>{item.question}</h3>
                <p>{item.answer}</p>
              </article>
            ))}
          </div>
        </section>
      </main>

      <footer className="footer">
        <div>
          <strong>InfraPilot</strong>
          <p>Open-source self-service infrastructure portal for governed operational workflows.</p>
        </div>
        <div className="footer-links">
          <a href={repositoryUrl} target="_blank" rel="noreferrer">GitHub</a>
          <a href="#/docs/quick-start">Quick start</a>
          <a href="#/docs/integration-setup">Integration setup</a>
          <a href="#/docs/introduction">Docs</a>
          <a href="#use-cases">Use cases</a>
          <a href="#examples">Examples</a>
        </div>
      </footer>
    </div>
  );
}

function Header() {
  return (
    <header className="topbar">
      <a className="brand" href="#home" aria-label="InfraPilot home">
        <span className="brand-mark">IP</span>
        <span>
          <strong>InfraPilot</strong>
          <small>Self-service infrastructure portal</small>
        </span>
      </a>

      <nav className="nav" aria-label="Primary">
        {navItems.map((item) => (
          <a key={item.href} href={item.href}>
            {item.label}
          </a>
        ))}
      </nav>
    </header>
  );
}

function DataTable({ columns, rows }: { columns: TableColumn[]; rows: TableRow[] }) {
  return (
    <div className="table-wrap">
      <table className="docs-table">
        <thead>
          <tr>
            {columns.map((column) => (
              <th key={column.key}>{column.label}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row, index) => (
            <tr key={index}>
              {columns.map((column) => (
                <td key={column.key}>
                  <InlineCodeText text={row[column.key] ?? ''} />
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function InlineCodeText({ text }: { text: string }) {
  const parts = text.split(/(`[^`]+`)/g).filter(Boolean);

  return (
    <>
      {parts.map((part, index) =>
        part.startsWith('`') && part.endsWith('`') ? (
          <code key={index}>{part.slice(1, -1)}</code>
        ) : (
          <span key={index}>{part}</span>
        ),
      )}
    </>
  );
}

export default App;
