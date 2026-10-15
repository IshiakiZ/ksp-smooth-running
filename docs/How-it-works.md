# How it works

## One terrain update per world

**What was measured** (2026-10-14, in the game on a Mac, at the launch pad, with a 230-part craft and every mod of the
workspace installed, after a session of loads and scene changes): a frame took 25.8 ms, waiting on the processor. The
dearest thing the game itself did was its terrain coroutine, `PQS.UpdateSphere`: 6.0 ms of every frame. Kerbin's
terrain (its quad tree) was being updated 75 times a frame.

**Why** (the game's own code, decompiled): a world's terrain update is started as `StartCoroutine(UpdateSphere())`, the
routine itself, at every start of the sphere (every load, every scene change, every time the world is brought back), and
stopped with `StopCoroutine("UpdateSphere")`, by its name. Unity's stop by name ends only coroutines that were started by
name: none ever is, so each start adds one more update that runs until the game is closed.

**The fix:** the newest update started for each sphere is noted, and an older one still running for the same sphere ends
at its next step, doing nothing more. Measured again in the same place: the terrain's update took 0.23 ms a frame.

**Its twin:** the sphere's other coroutine, `ResetAndWaitCoroutine` (started at every reset, stopped the same way by
name), leaks alike: 28 of them were found ticking every frame for the worlds not in use, each waiting for its world to
come alive. They are cheap while they wait; they are mended the same way.

**KSP Community Fixes** mends the same flaw in its own way. With it installed only one update is ever running, and this
fix changes nothing.

## On the mod's page

In the Keystone window (the keystone button on the game's toolbar, or Alt-K; Option-K on a Mac), under Smooth Running: a
switch for each fix, and how many extra terrain updates it has ended in this game.

## Limits

* One fix so far. The rest of the measured frame is the game's own physics and drawing, and each further fix waits on
  its own measurement.
* Tried on a Mac only. On Windows it is the same code (it has no shaders), and nobody has run it there yet.
