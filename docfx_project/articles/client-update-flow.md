On the Client: The client continuously receives state updates from the server. The ClientEntityManager on each client handles applying these:

* Interpolation: For remote entities (ones the client is not controlling), LES uses an interpolation buffer to smooth out movement
    github.com
    . Each update from the server comes with a tick timestamp; the client buffers a small amount of state and renders entities a bit behind the latest tick to interpolate between received positions. This results in smooth motion instead of jittery, frame-by-frame updates.

* Prediction & Reconciliation: For the local player’s own entity, the client may have predicted ahead. When the server’s update for that entity arrives, LES will reconcile any differences. If the server’s authoritative state differs from the client’s predicted state (e.g. due to unaccounted physics or corrections), the client will adjust. Minor differences might be corrected smoothly (slight position nudges) rather than instant teleportation, to hide latency.

* SyncVar Updates: LES supports synchronized variables (“SyncVars”). When these change on the server, the new values are included in the delta update and the client applies them to its entity objects. Optional change callbacks (ISyncFieldChanged) can trigger client-side effects on update (e.g. updating a health bar when health changes).