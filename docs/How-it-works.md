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

## Handlers of things that are gone (0.2.0, 2026-10-17)

**What was measured** (the test save, rounds of scene changes: space centre, assembly building, space centre, a
flight): every round left about 420 more handlers on the game's events (a handler: someone the game tells when
something happens), 4,200 after the third round against 2,950 at the start. Some 330 of each round's were the game's
own records of which parts share resources (`PartSet`), two each. Most of the rest belonged to things already
destroyed: a craft's network link, the staging list's groups, the editor's own logic.

**Why** (the game's own code, decompiled): such a record adds itself to two events when it is made and takes itself off
only in its finaliser, which never runs, because the events themselves keep it alive. So every one ever made stays,
told of every change to a resource's flow, keeping the parts of craft destroyed long ago in memory for the collector to
go through at every collection. And the game takes a handler off an event only when what it belongs to is gone
altogether, while a destroyed object of Unity's is still something to the code that checks.

**The fix:** once each scene is ready, every one of the game's events is gone through, and a handler is taken off if
what it belongs to is a destroyed object, or one of those records whose craft is destroyed or whose every part is. A
record that is empty, or whose craft is being built in the editor with no parts yet, is left alone; nothing else is
touched. Measured: 363 taken off at one change from a flight to the space centre, 332 of them part sets'; with it, a
round of scene changes left 18 more handlers instead of 420, and the managed memory freed at one clean-up was 15 MB.

## One clean-up fewer at every scene change (0.2.0, 2026-10-17)

**What was measured** (from a flight to the space centre): Unity went through every one of its loaded things for any no
longer used three times, 190 to 200 ms each with the game's quarter of a million things, nearly all of it in marking
what is still used. The first freed the 3,727 things of the flight; the second freed nothing; the third, once the space
centre was in, freed 11.

**Why:** the game changes scenes by loading a scene that is all but empty first (its "loading buffer") and then the one
asked for. Unity goes through its things by itself every time a scene takes another's place; and the game, in the same
step as asking for the buffer, asks for one more (`HighLogic.bufferedLoad`), which comes right after Unity's own with
nothing having changed in between.

**The fix:** that one call of the game's is left out, in both of its ways of changing scenes; Unity's own, after the
buffer is in, still frees everything the last scene left. Measured: two goings-through instead of three (3,730 things
freed, then 11), and the change from a flight to the space centre took 3.22 s to the space centre's last mod being ready
instead of 3.36.

**KSP Community Fixes** has a fix of its own for this (by its own account, one going-through left of three). This one
only leaves out the game's own extra call and never touches Unity's, so with both installed nothing is lost.

## On the mod's page

In the Keystone window (the keystone button on the game's toolbar, or Alt-K; Option-K on a Mac), under Smooth Running: a
switch for each fix, and what each has done in this game (extra terrain updates ended, clean-ups left out, handlers
taken off).

## Limits

* Three fixes so far. The rest of the measured frame is the game's own physics and drawing, and each further fix waits
  on its own measurement. A scene change still has two goings-through of Unity's things: the last, after the scene
  asked for is in, is Unity's own and could be left out only by changing how the game loads its scenes.
* Tried on a Mac only. On Windows it is the same code (it has no shaders), and nobody has run it there yet.
