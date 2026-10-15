# Smooth Running

The game made faster where it wastes its time, each fix measured in the game first.

![What the game's terrain update costs each frame, as the game ships and with Smooth Running: 6.0 ms and 0.23 ms](src/SmoothRunning/img/smooth-running.png)

For Kerbal Space Program 1.12.x. **Needs [Keystone](https://github.com/IshiakiZ/ksp-keystone)** ([on SpaceDock](https://spacedock.info/mod/4665/Keystone))
and **Harmony** (HarmonyKSP), both separate downloads: this mod changes some of the game's own code as it runs (nothing on
disk), and does not load without them.

## What it mends

* **The terrain's update, once per world.** The game starts a world's terrain update again at every load, every scene
  change and every revert, and never stops the last one: after an evening of play Kerbin's terrain was being updated 75
  times a frame, 6 ms of every frame on a Mac. It ends the extra copies. (KSP Community Fixes mends the same flaw in its
  own way; with it installed this changes nothing.)

More to come, each only after it has been measured.

## Install

Copy the `SmoothRunning` folder from the download into your KSP's `GameData`, beside `Keystone` and Harmony's `000_Harmony`.

## Use

It is on as installed. In the Keystone window (the keystone button on the game's toolbar, or Alt-K; Option-K on a Mac),
under **Smooth Running**: a switch for each fix, and how many extra terrain updates it has ended this game.

![Smooth Running's page in the Keystone window: the switch for the terrain fix and what it has done](src/SmoothRunning/img/smooth-running-page.jpg)

## Works on

Mac: tried. Windows 11: the same code (no shaders), not run there. Linux: never run.

## Licence

[CC BY 4.0](LICENSE): use it, change it and pass it on as you like, as long as you credit Ishiaki and link back here.
