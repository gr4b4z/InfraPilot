# 06: Webhooks for freeze and unfreeze

Status: ready-for-agent
Blocked by: 01

**What to build:** External systems (Slack, Teams, CI) can react to a Promotion Freeze starting or
ending. Two new webhook event types, `promotion.frozen` and `promotion.unfrozen`, are dispatched when
an administrator creates or lifts a freeze, filtered by both product and environment like the other
promotion events, with a payload carrying the pair, the reason (on freeze), the actor and the time.

- [ ] Creating a freeze dispatches `promotion.frozen` to subscriptions matching the product and environment filters
- [ ] Lifting a freeze dispatches `promotion.unfrozen` the same way
- [ ] A subscription filtered to another product or environment does not receive the event
- [ ] Webhook dispatch failure does not fail the freeze/unfreeze request (same non-fatal behaviour as existing promotion webhooks)
- [ ] `notes/promotions-api.md` and the webhook documentation list the two new event types and their payloads
