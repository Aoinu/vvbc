# Virtual Venue Baseball Components

Virtual Venue Baseball Components

## Development setup

Clone the project and its package submodules:

```powershell
git clone --recurse-submodules https://github.com/Aoinu/vvbc.git
```

For an existing clone, run:

```powershell
git submodule update --init --recursive
```

`Packages/org.vvbc.assets` is the Git submodule for [Aoinu/vvbc-assets](https://github.com/Aoinu/vvbc-assets). Develop Core and Assets in the same Unity project, with the same ProjectSettings and VRChat SDK environment. The Assets package is managed and distributed as an independent VPM package.

The [Bat system design](docs/development/bat-system.md) describes the current implementation and its validation limits.
