---
title: Rollbacks skip versions whose image was removed
date: 2026-11-09
audience: everyone
link: /rollbacks
linkLabel: Rollbacks
---
When an old version's image has been cleaned out of the container registry, the portal no longer
offers it as a rollback target. **Roll back to version** leaves it out, and a rollback or
**Roll back to last known good version** that would need it explains that its image was removed.
