# T# Prefab field overrides

## Purpose

VVBC's T# Behaviours run as `UdonBehaviour` components. The SDK stores their
public variable table as one serialized blob; Unity therefore treats any change
to that table as an override of the whole table. VVBC needs authors to edit
individual T# Inspector fields in the existing Inspector while non-overridden
fields continue to inherit changes from the source Prefab.

## Required behavior

- Apply to every T# Program Asset in `org.vvbc.core`, including programs created
  later, without replacing T# source as the authority for the program schema.
- Present field editing, individual override state, and individual revert/apply
  through Unity's normal Prefab property machinery in the Udon Inspector.
- Support base Prefabs, nested Prefabs, Variants, Scene instances, and ordinary
  non-Prefab UdonBehaviours. Preserve references to Scene and Prefab objects,
  arrays, nulls, AnimationCurve keys and tangents, and existing values.
- When migrating an existing blob override, preserve fields whose values differ
  from the immediate source as individual overrides; equal values inherit.
  Historical intent for an override equal to the source cannot be recovered.
- Synchronize inherited and overridden authoring values to Udon public variables
  before play and before a world build. Udon programs remain the only runtime
  implementation. No authoring helper may run in an uploaded world.
- Do not patch or redistribute the TsukimiSharp or VRChat packages. Do not
  automatically save an unrelated dirty Scene or alter `org.vvbc.assets`.

## Design constraints established in Unity

The SDK serializes `UdonBehaviour.publicVariables` into a single string and an
object reference list. Changing the SDK table alone therefore cannot produce
field-level Unity Prefab overrides. A Unity-serialized per-field authoring
representation is needed, with stable field identities across schema changes.
An isolated Unity experiment confirmed that a serialized list entry can be
overridden independently, that other entries continue to inherit from a parent
Prefab, and that entries appended to the parent are inherited by a Variant.
Existing entries must not be reordered or removed when the schema evolves.

The SDK dispatches a custom Udon Inspector by the exact Program Asset type,
and TsukimiSharp already owns the Inspector for `TsukimiProgramAsset`. An
isolated experiment confirmed that a subtype can preserve a T# Program's serialized
source and Udon program and use the SDK's public custom Inspector extension.
Migrating a copied Program Asset to that subtype preserved its GUID. Any actual
migration must preserve existing asset GUIDs and all UdonBehaviour references.

Unity refuses to attach a MonoBehaviour compiled only for the Editor to a
Prefab. Authoring data must therefore be attached in a form Unity can serialize
and removed from builds, rather than relying on an Editor-only assembly class.
Build validation must prove that its removal does not remove the Udon data it
produced or referenced objects.

## Validation

In addition to the gates in [the T# toolchain](tsharp-toolchain.md), exercise
per-field editing and revert, same-value and differing-value legacy migration,
inheritance after parent edits, added fields, arrays, curves, object references,
nested Variants, Scene save/reopen, Undo/Redo, domain reload, and build-time
serialization. Tests must use disposable Prefabs and Scenes, not Sandbox
assets. Validate the resulting Udon public variables and actual generated Udon
programs in both the development project and clean consumers. VRChat client
behavior must be identified separately when it cannot be tested locally.
