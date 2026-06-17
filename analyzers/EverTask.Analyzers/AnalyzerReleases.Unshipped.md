; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category               | Severity | Notes
--------|------------------------|----------|-------------------------------------------------------------------------
ET0001  | EverTask.Serialization | Warning  | Public field on a payload type is not serialized by System.Text.Json
ET0002  | EverTask.Serialization | Warning  | Property with a non-public setter and no matching ctor parameter is dropped on recovery
ET0003  | EverTask.Serialization | Warning  | Newtonsoft.Json attribute is ignored by System.Text.Json
ET0004  | EverTask.Serialization | Warning  | Abstract/interface payload property without declared STJ polymorphism throws on recovery
ET0005  | EverTask.Serialization | Info     | object / Dictionary&lt;string,object&gt; property deserializes to JsonElement
ET0006  | EverTask.Serialization | Disabled | Property of a type that is unlikely to round-trip
ET0007  | EverTask.Serialization | Warning  | Type has multiple public constructors but none usable by System.Text.Json
