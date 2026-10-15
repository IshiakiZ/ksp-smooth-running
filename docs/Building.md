# Building

`./build.sh` builds the mod and installs it into the game; `./build.sh check` only compiles; `./build.sh dist` builds
into `GameData/` here without touching the game. It needs the .NET SDK, a copy of the game (`KSP_DIR=/path` if it is not
where Steam puts it), `Keystone.dll` from [Keystone](https://github.com/IshiakiZ/ksp-keystone) (the copy installed in the
game, a built copy of that repository beside this one, or `KEYSTONE=/path/to/Keystone.dll`) and Harmony's `0Harmony.dll`
(HarmonyKSP, installed in the game's `GameData/000_Harmony`).

This repository is made from a workspace that holds several mods side by side; changes arrive here from there.
