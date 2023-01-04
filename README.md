# LiteEntitySystem
Pure C# HighLevel API for multiplayer games using .NET Standard 2.1

**Warning: there is some things "to do" and there is some known bugs, but because i use for my own games they will be fixed asap**

[![Made in Ukraine](https://img.shields.io/badge/made_in-ukraine-ffd700.svg?labelColor=0057b7)](https://stand-with-ukraine.pp.ua)

**Discord chat**: [![Discord](https://img.shields.io/discord/501682175930925058.svg)](https://discord.gg/FATFPdy)

[Little Game Example on Unity](https://github.com/RevenantX/LiteEntitySystemUnityExample)

[Documentation](https://revenantx.github.io/LiteEntitySystem/api/LiteEntitySystem.html)

## Features

* .NET Standard 2.1 and pure C# (but with some IL magic)
* Can be used with Unity (2021.2 and later), Godot, Monogame or just pure .net
* Can be used for creation any multiplayer game (2d,3d,4d,...)
* Works with Unity IL2CPP
* No code generation
* Epic speed
* Lag compensation
* Serialization of custom types (like strings,lists,arrays,jsons,etc)
* Synchronized variables (with optional notifications on change)
* Client-side prediction
* Client-side spawn prediction (for projectiles)
* Remote procedure calls (RPC) with compile-time checks
* Client input system
* Basic hierarchy system (childs, parent)
* Controllers and Pawns concept
* Interpolation system
* Delta-compressed state synchronization
* LZ4 compression of initial world state
* Also works as game logic engine
* LiteNetLib as core transport with tight integration and without abstractions for maximum speed

## Unity notes!!!
* Always use library sources instead of precompiled DLL files ( because there are platform specific #ifdefs and workarounds for unity bugs )
