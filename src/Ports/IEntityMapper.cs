using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Ports;

public sealed class UnsupportedEntityWriteParentMappingException()
    : InvalidOperationException(
        "The configured entity mapper does not support approved parent evidence.")
{
    public string SafeCode => "ENTITY_WRITE_PARENT_MAPPING_UNSUPPORTED";
}

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
        EntityWriteParent? resolvedParent)
    {
        if (resolvedParent is not null)
            throw new UnsupportedEntityWriteParentMappingException();
        return MapCreate(source, targetVendor, targetEntityType, options);
    }
    EntityWriteRequest MapUpdate(ExternalEntity source, ExternalEntity target, MatchOptions options);
}
