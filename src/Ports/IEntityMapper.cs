using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public interface IEntityMapper
{
    EntityWriteRequest MapCreate(
        ExternalEntity source,
        string targetVendor,
        string targetEntityType,
        MatchOptions options);
    EntityWriteRequest MapCreate(
        ExternalEntity source,
        string targetVendor,
        string targetEntityType,
        MatchOptions options,
        EntityWriteParent? resolvedParent) =>
        MapCreate(source, targetVendor, targetEntityType, options);
    EntityWriteRequest MapUpdate(ExternalEntity source, ExternalEntity target, MatchOptions options);
}
