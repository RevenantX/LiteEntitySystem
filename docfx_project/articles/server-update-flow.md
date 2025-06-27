# ServerEntityManager update flow

The server runs the game simulation and is the source of truth for entity states. It processes ticks (frames of game logic) at a fixed rate.

The server sends out state update packets to each client at a regular rate (configured [`SendRate`](xref:LiteEntitySystem.ServerEntityManager.SendRate) and [`Tickrate`](xref:LiteEntitySystem.EntityManager.Tickrate)). These include updated RPCs, positions, velocities, health, etc., for entities relevant to that client. 

Every delta-state that server send to clients contains:
* ServerTick
* Last processed (applied) client input tick
* All pending RPCs sincle last recieved server tick by player
* Entity SyncVar fields data that was changed since last acknowledged tick by player

Each tick, the server does the following in `ServerEntityManager.LogicUpdate` (called internally from [`ServerEntityManager.Update`](xref:LiteEntitySystem.EntityManager.Update) public method):

### 1. Read players `ClientRequests`

* Read client requests generated on client using:
<xref:LiteEntitySystem.HumanControllerLogic.SendRequest``1(``0)>,
<xref:LiteEntitySystem.HumanControllerLogic.SendRequestStruct``1(``0)>

* Executes actions passed into server side methods (in order as client sent):
<xref:LiteEntitySystem.HumanControllerLogic.SubscribeToClientRequest``1(System.Action{``0})>,
<xref:LiteEntitySystem.HumanControllerLogic.SubscribeToClientRequestStruct``1(System.Func{``0,System.Boolean})> 

**Notice** Currently this requests can arrive not in sync with player Input data because they sent using Reliable-Ordered network channel. Where `Inputs` sent unreliably with some special technics to improve reliability

### 2. Apply players `Input`

Apply pending inputs (created on client using [`HumanControllerLogic.ModifyPendingInput`](xref:LiteEntitySystem.HumanControllerLogic`1.ModifyPendingInput)) from all connected players using their controllers based on [`HumanControllerLogic`](xref:LiteEntitySystem.HumanControllerLogic) (LES’s client input system transmits each client’s controller inputs to the server every tick)

Writes incoming input data to [`HumanControllerLogic.CurrentInput`](xref:LiteEntitySystem.HumanControllerLogic`1.CurrentInput)

For AI or bots typically inputs directly passed in Update logic to controlled entities

### 3. `Update` all updateable Entities
* Update the game state by running each entity’s logic that marked by [`[EntityFlags.Updateable]`](xref:LiteEntitySystem.EntityFlags.Updateable) or [`[EntityFlags.UpdateOnClient]`](xref:LiteEntitySystem.EntityFlags.UpdateOnClient). For example, the server updates player pawn positions based on controller input, moves projectiles, checks collisions, etc. This is done in the entity classes [`InternalEntity.Update`](xref:LiteEntitySystem.Internal.InternalEntity.Update) method. The server is authoritative, so these updates determine the true state (e.g. whether a shot hit a target).

* Execute [`OnLateConstructed`](xref:LiteEntitySystem.Internal.InternalEntity.OnLateConstructed) method for all entities created on current logic tick

* Refresh newly created `Construct RPCs` with latest entity data from current logic tick

    * `Construct RPC` contains all `SyncVar` data. So this data updated after all entities update

* Write entity data history for `LagCompensation`

Entitites created at current logic tick will not be included in this `Update` list

### 4. Send logic preparations

Check that current server state should be sent depending on send rate and PlayersCount (skip send logic if 0)

### 5. Make and send baseline state

* For all new players make baseline state starting from executing <xref:LiteEntitySystem.Internal.InternalBaseClass.OnSyncRequested> methods for all `Entities` and `SyncableFields`
This method typically overrided in [`EntityLogic`](xref:LiteEntitySystem.EntityLogic)/[`SyncableField`](xref:LiteEntitySystem.SyncableField) - and calling `ExecuteRPCs`/`ExecuteClientAction` that should send some initial full sync data like full array data and size, dictionary info etc. 
Mostly used by classes inherited from [`SyncableField`](xref:LiteEntitySystem.SyncableField).

* Send baseline state with full data for newly connected players or players requested baseline state because of big lag/unsync
 
    * For new players - `NewRPC` (calling `new Entity`), `ConstructRPCs` (calling `OnConstructed`) and rpcs created only inside <xref:LiteEntitySystem.Internal.InternalBaseClass.OnSyncRequested> will be sent

    * For old players - all pending RPCs will be sent to maintain full RPC reliability

### 6. Make and send delta states

This part executes for all old players that already received baseline state

* Put in packet all pending RPCs that client didn't received yet. Each RPCs has tick info
so client can execute them gradually over time to prevent shoot bursts on lags and similar things

* Put delta-compressed state for each client.
It compares each entity’s current state to what that client last acknowledged and serializes only the changes (position deltas, changed variables, etc.). This is efficient for bandwidth.

You can mark entity RPCs and `SyncVars` to join `SyncGroups` to dynamically enable/disable replication for selected groups and entities for selected players.

### 7. Trigger network send

Triggers network library (or custom stransport) Flush/Send logic to reduce latency.