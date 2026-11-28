# Support and realtime notifications

ITickets and TicketOperations provide inbox, creation, replies and status changes. The web pages include Tickets, NewTicket, TicketDetail and Messages. TicketOperation.GetAsync finds a ticket in the authorized inbox rather than bypassing that inventory.

## Backend behavior

DirectAdmin TicketService combines received tickets, outgoing requests and messages through upstream API calls and writes through legacy ticket operations. Preserve source identity, partial-inventory behavior and upstream permissions. KubernetesTicketService persists tickets, replies and messages in SQLite, but its current inbox, reply and status operations are scoped to the ticket owner; messages are scoped to the recipient. A stored Recipient field does not establish a complete administrator/reseller support queue or delivery workflow. The returned Kubernetes ticket source is request. Do not claim full DirectAdmin support parity.

## Live activity

KubernetesTicketService saves ticket creation, replies and status changes to SQLite before publishing an account-scoped invalidation to TicketEvents. TicketSignalDispatcher is the single channel consumer and sends only a generic InboxChanged notification to active circuit tokens registered for that account. The frontend reads the authorized inbox again; no ticket content or credentials travel in the notification. There is no Kubernetes ticket polling.

TicketLiveMonitor starts only for DirectAdmin, where upstream push is not available. Its 15-second observation detects changes for the effective identity and publishes a circuit-scoped invalidation into the same channel; it never sends SignalR messages itself. DirectAdmin remains authoritative and is not mirrored in SQLite.

PanelSession rotates subscription tokens on identity changes and revokes them on disconnect/disposal. The dispatcher checks active subscriptions and session connectivity before sending. The SignalR component rejoins after reconnect and invalidates its inbox to recover missed changes. Notifications are in-process, best effort and not a durable delivery queue; the ticket database is durable. Existing owner-only support routing is unchanged.

HostingOperationNotifications is a separate in-process invalidation event for hosting reconciliation, not the ticket hub and not an event stream containing account data.

The ticket overview owns a LatestRead instance and subscribes to session and inbox invalidation events. Notifications received during a read request one follow-up refresh rather than starting overlapping reads. Session changes clear the ticket inventory, message count and warnings immediately, cancel the old read and reload with the new effective identity. Disposal removes subscriptions and cancels reads. Failed inbox reads clear the associated inventory rather than displaying counts from an earlier result. The notification transport and provider authorization are unchanged.
