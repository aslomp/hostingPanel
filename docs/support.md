# Support and realtime notifications

ITickets and TicketOperations provide inbox, creation, replies and status changes. The web pages include Tickets, NewTicket, TicketDetail and Messages. TicketOperation.GetAsync finds a ticket in the authorized inbox rather than bypassing that inventory.

## Backend behavior

DirectAdmin TicketService combines received tickets, outgoing requests and messages through upstream API calls and writes through legacy ticket operations. Preserve source identity, partial-inventory behavior and upstream permissions. KubernetesTicketService persists tickets, replies and messages in SQLite, but its current inbox, reply and status operations are scoped to the ticket owner; messages are scoped to the recipient. A stored Recipient field does not establish a complete administrator/reseller support queue or delivery workflow. The returned Kubernetes ticket source is request. Do not claim full DirectAdmin support parity.

## Live activity

TicketLiveMonitor is a circuit-scoped asynchronous monitor, started by TicketNotifications. It polls TicketOperations.InboxAsync every 15 seconds while a session is active; despite its original DirectAdmin-oriented comment it calls the selected provider. The initial successful observation establishes a baseline without announcing historical tickets. It tracks ticket reply signatures and message presence; changes publish InboxChanged over TicketHub (/hubs/tickets).

TicketChannelRegistry issues circuit-bound subscription tokens and supports revocation. PanelSession rotates the token on identity changes and revokes it on disconnect/disposal. The monitor checks the active effective channel before publishing. TicketNotifications manages a SignalR connection, rejoins after reconnect, increments the badge, shows a snackbar and notifies the circuit to refresh its inbox. Normal ticket pages still work if the hub is unavailable.

The SignalR client in the Razor component runs on the server-side interactive circuit; it is not a direct DirectAdmin push endpoint. The application uses polling upstream. Do not send credentials on the hub. Subscription tokens and notification payloads must be treated as private; do not assume the token registry itself provides complete connection revocation or production authorization assurance. Verify cross-session isolation and reconnect/disposal behavior in tests before extending the hub.

HostingOperationNotifications is a separate in-process invalidation event for hosting reconciliation, not the ticket hub and not an event stream containing account data.

The ticket overview owns a LatestRead instance and subscribes to session and inbox invalidation events. Notifications received during a read request one follow-up refresh rather than starting overlapping reads. Session changes clear the ticket inventory, message count and warnings immediately, cancel the old read and reload with the new effective identity. Disposal removes subscriptions and cancels reads. Failed inbox reads clear the associated inventory rather than displaying counts from an earlier result. The notification transport and provider authorization are unchanged.
