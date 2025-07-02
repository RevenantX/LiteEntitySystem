# LiteEntitySystem

**LiteEntitySystem** (LES) provides a high-level framework for multiplayer game entities with built-in networking via LiteNetLib (custom transports supported). 

In a typical setup the server manages the authoritative game state using a <xref:LiteEntitySystem.ServerEntityManager>, while each client maintains a local mirror of the game world via a <xref:LiteEntitySystem.ClientEntityManager>.

The game logic is implemented in custom entity classes based on:
* <xref:LiteEntitySystem.EntityLogic> - for most simple entities like weapons, grenades, usable objects, doors, etc
* <xref:LiteEntitySystem.PawnLogic> for player-controlled entities like PlayerEntity that has position, health, money, etc
* <xref:LiteEntitySystem.HumanControllerLogic`1> for player controllers that generate input and also make Client->Server requests
* <xref:LiteEntitySystem.AiControllerLogic> for AI controllers

## Usefull articles to read

[Client update flow](articles/client-update-flow.md)

[Server update flow](articles/server-update-flow.md)

## [API documentation](api/index.md)

