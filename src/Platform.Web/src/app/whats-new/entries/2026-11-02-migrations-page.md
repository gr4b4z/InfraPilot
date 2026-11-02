---
title: See every database migration on the new Migrations page
date: 2026-11-02
audience: everyone
link: /migrations
linkLabel: Migrations
---
A new **Migrations** page, just below **Deployments**, lists the database migrations of every deployment: the schema migration that ran during the deployment and the data migration that runs after it. Each one opens to the individual migrations, which applied and which failed, and the output they printed. A data migration shows as **Pending** until it runs, and an older one shows as **Superseded** once a newer version's replaces it.

Use **Find a migration** at the top of the page to check whether a migration has run yet, and where. Part of the name or a misspelling is enough.
