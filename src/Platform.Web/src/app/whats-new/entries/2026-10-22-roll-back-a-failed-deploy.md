---
title: Roll back a failed deploy in one click
date: 2026-10-22
audience: everyone
link: /rollbacks
linkLabel: Rollbacks
---
When a deployment fails, its page — and the promotion that shipped it — now offers **Roll back to last known good version**. The portal works out which version last deployed successfully and raises the rollback for you; the confirmation tells you who has to approve it. If the service never deployed successfully to that environment, you'll see **Remove from *environment*** instead.

Approving the rollback also puts the failed version's promotion back to Pending, so it can be approved again once it's fixed.
