---
title: See when a failed deploy holds back data migrations
date: 2026-11-12
audience: everyone
link: /releases
linkLabel: Releases
---
When a service fails to deploy, the rest of that deploy run's data migrations wait for the next run.
A release's **Run schema and data migrations** step now says so in a warning, names the service that
failed, and shows **waits for the next run** next to each held-back service until the next run has
run them.
