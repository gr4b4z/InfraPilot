---
title: Change failure rate and time to restore, measured
date: 2026-10-26
audience: everyone
link: /analytics/delivery
linkLabel: Delivery metrics
---
Once an admin records production incidents, the **Delivery metrics** report fills in the last two DORA figures: **Change failure rate** counts the deployments that caused an incident, and **Time to restore service** is how long those incidents took to resolve. Each incident is tied to a deployment by the deploy or version named on it, or else to whatever was running when it started; the card's ⓘ says which rule applied and how many incidents it could tie. Admins also see the incidents behind the numbers at the bottom of the report, and tune the matching under **Settings → Delivery Metrics**.
