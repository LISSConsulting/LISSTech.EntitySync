using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace LISSTech.EntitySync.Core;

internal static class EntitySyncPlanWorkbook
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly string[] DecisionOptions =
    {
        "Accept Planned",
        "Reject",
        "Create",
        "Link",
        "Update",
        "No Update",
        "Review"
    };

    public static void Write(EntitySyncPlan plan, string path)
    {
        plan = EntitySyncPlanArtifactSanitizer.Sanitize(plan);
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteText(archive, "[Content_Types].xml", ContentTypesXml());
        WriteText(archive, "_rels/.rels", RootRelationshipsXml());
        WriteText(archive, "xl/workbook.xml", WorkbookXml());
        WriteText(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationshipsXml());
        WriteText(archive, "xl/worksheets/_rels/sheet1.xml.rels", ReviewSheetRelationshipsXml());
        WriteText(archive, "xl/styles.xml", StylesXml());
        WriteText(archive, "xl/theme/theme1.xml", ThemeXml());
        WriteText(archive, "xl/tables/table1.xml", ReviewTableXml(plan));

        using (var stream = archive.CreateEntry("xl/worksheets/sheet1.xml").Open()) WriteReviewSheet(plan, stream);
        using (var stream = archive.CreateEntry("xl/worksheets/sheet2.xml").Open()) WritePlanSheet(plan, stream);
        using (var stream = archive.CreateEntry("xl/worksheets/sheet3.xml").Open()) WriteListSheet(stream);
        using (var stream = archive.CreateEntry("xl/worksheets/sheet4.xml").Open()) WriteTargetSheet(plan, stream);
        using (var stream = archive.CreateEntry("xl/worksheets/sheet5.xml").Open()) WriteLegendSheet(stream);
    }

    public static EntitySyncPlan Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var sharedStrings = ReadSharedStrings(archive);
        var plan = ReadPlan(archive, sharedStrings);
        ApplyReview(archive, plan, sharedStrings);
        return plan;
    }

    private static void WriteReviewSheet(EntitySyncPlan plan, Stream stream)
    {
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
        var headers = HeadersForPlan(plan);
        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        writer.WriteAttributeString("xmlns", "r", null, RelationshipsNamespace);
        writer.WriteStartElement("sheetViews");
        writer.WriteStartElement("sheetView");
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteStartElement("pane");
        writer.WriteAttributeString("ySplit", "1");
        writer.WriteAttributeString("topLeftCell", "A2");
        writer.WriteAttributeString("activePane", "bottomLeft");
        writer.WriteAttributeString("state", "frozen");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("sheetFormatPr");
        writer.WriteAttributeString("defaultRowHeight", "17");
        writer.WriteAttributeString("customHeight", "1");
        writer.WriteEndElement();
        writer.WriteStartElement("cols");
        WriteReviewColumns(writer, headers);
        writer.WriteEndElement();
        writer.WriteStartElement("sheetData");
        WriteRow(writer, 1, headers.Cast<object?>().ToArray(), 1);
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i];
            WriteRow(writer, i + 2, ReviewValues(plan, item, i + 1));
        }
        writer.WriteEndElement();
        WriteSourceTargetMismatchConditionalFormatting(writer, headers.Length);
        var targetCount = TargetList(plan).Count;
        writer.WriteStartElement("dataValidations");
        writer.WriteAttributeString("count", targetCount > 0 ? "2" : "1");
        writer.WriteStartElement("dataValidation");
        writer.WriteAttributeString("type", "list");
        writer.WriteAttributeString("allowBlank", "1");
        writer.WriteAttributeString("showErrorMessage", "1");
        writer.WriteAttributeString("errorTitle", "Invalid decision");
        writer.WriteAttributeString("error", "Choose a value from the dropdown list.");
        writer.WriteAttributeString("sqref", "B2:B1048576");
        writer.WriteElementString("formula1", $"'_Lists'!$A$1:$A${DecisionOptions.Length}");
        writer.WriteEndElement();
        if (targetCount > 0)
        {
            writer.WriteStartElement("dataValidation");
            writer.WriteAttributeString("type", "list");
            writer.WriteAttributeString("allowBlank", "1");
            writer.WriteAttributeString("showErrorMessage", "1");
            writer.WriteAttributeString("errorTitle", "Invalid target");
            writer.WriteAttributeString("error", "Choose a target from the dropdown list.");
            writer.WriteAttributeString("sqref", "F2:F1048576");
            writer.WriteElementString("formula1", $"'_Targets'!$A$1:$A${targetCount}");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteStartElement("tableParts");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("tablePart");
        writer.WriteAttributeString("r", "id", RelationshipsNamespace, "rId1");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteSourceTargetMismatchConditionalFormatting(XmlWriter writer, int columnCount)
    {
        writer.WriteStartElement("conditionalFormatting");
        writer.WriteAttributeString("sqref", $"A2:{ColumnName(columnCount - 1)}1048576");
        writer.WriteStartElement("cfRule");
        writer.WriteAttributeString("type", "expression");
        writer.WriteAttributeString("dxfId", "0");
        writer.WriteAttributeString("priority", "1");
        writer.WriteElementString("formula", "AND($F2<>\"\",$E2<>$F2)");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static string[] HeadersForPlan(EntitySyncPlan plan)
    {
        var headers = new List<string>
        {
            "Item",
            "Decision",
            "PlannedAction",
            "Status",
            EntityNameHeader(plan.SourceVendor, plan.SourceEntityType),
            EntityNameHeader(plan.TargetVendor, plan.TargetEntityType)
        };

        if (NeedsSiteClientContext(plan))
        {
            headers.Add("HaloClientName");
            headers.Add("NCentralCustomerName");
            headers.Add("HaloClientId");
            headers.Add("NCentralCustomerId");
        }

        headers.AddRange(new[] { "Score", "MatchType", "Reasons", "SourceId", "TargetId", "SourceEmail", "TargetEmail", "ReviewerNotes" });
        return headers.ToArray();
    }

    private static object?[] ReviewValues(EntitySyncPlan plan, EntitySyncPlanItem item, int itemNumber)
    {
        var values = new List<object?>
        {
            itemNumber,
            string.Empty,
            item.Action,
            item.Status,
            item.Source.Name,
            item.Target == null ? null : TargetDisplay(item.Target)
        };

        if (NeedsSiteClientContext(plan))
        {
            values.Add(SourceClientName(item.Source));
            values.Add(TargetCustomerName(item));
            values.Add(item.Source.GetExternalId("HaloPsaClientId"));
            values.Add(item.Target?.GetExternalId("NCentralCustomerId"));
        }

        values.AddRange(new object?[] { item.Score, item.MatchType, string.Join("; ", item.Reasons), item.Source.Id, item.Target?.Id, item.Source.Email, item.Target?.Email, string.Empty });
        return values.ToArray();
    }

    private static string EntityNameHeader(string vendor, string entityType) => VendorPrefix(vendor) + entityType + "Name";

    private static string VendorPrefix(string vendor)
    {
        if (vendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)) return "Halo";
        return vendor;
    }

    private static bool NeedsSiteClientContext(EntitySyncPlan plan)
    {
        return plan.SourceVendor.Equals("HaloPSA", StringComparison.OrdinalIgnoreCase)
            && plan.SourceEntityType.Equals("Site", StringComparison.OrdinalIgnoreCase)
            && plan.TargetVendor.Equals("NCentral", StringComparison.OrdinalIgnoreCase)
            && plan.TargetEntityType.Equals("Site", StringComparison.OrdinalIgnoreCase);
    }

    private static void WritePlanSheet(EntitySyncPlan plan, Stream stream)
    {
        var json = JsonSerializer.Serialize(plan);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        writer.WriteStartElement("sheetData");
        var row = 1;
        foreach (var chunk in Chunk(encoded, 30000))
        {
            WriteRow(writer, row++, new object?[] { chunk });
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteListSheet(Stream stream)
    {
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        writer.WriteStartElement("sheetData");
        for (var i = 0; i < DecisionOptions.Length; i++) WriteRow(writer, i + 1, new object?[] { DecisionOptions[i] });
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteTargetSheet(EntitySyncPlan plan, Stream stream)
    {
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        writer.WriteStartElement("sheetData");
        var targets = TargetList(plan);
        for (var i = 0; i < targets.Count; i++) WriteRow(writer, i + 1, new object?[] { TargetDisplay(targets[i]) });
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteLegendSheet(Stream stream)
    {
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        writer.WriteStartElement("sheetViews");
        writer.WriteStartElement("sheetView");
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("sheetFormatPr");
        writer.WriteAttributeString("defaultRowHeight", "17");
        writer.WriteAttributeString("customHeight", "1");
        writer.WriteEndElement();
        writer.WriteStartElement("cols");
        WriteColumn(writer, 1, 1, 24);
        WriteColumn(writer, 2, 2, 100);
        writer.WriteEndElement();
        writer.WriteStartElement("sheetData");
        WriteRow(writer, 1, new object?[] { "Term", "Explanation" }, 1);
        WriteRow(writer, 2, new object?[] { "Score", "0-100 confidence score for the best candidate. 100 means an explicit external/integration ID link. Scores at or above AutoLinkScore can be planned as Link; scores at or above ReviewScore are review-worthy; lower scores are LowConfidence and are left unselected." });
        WriteRow(writer, 3, new object?[] { "MatchType", "Linked means an external/integration ID matched. HighConfidence means the weighted matcher met AutoLinkScore. NeedsReview means it met ReviewScore but still requires human review. LowConfidence means the best candidate was below ReviewScore. NoMatch means no usable candidate was found." });
        WriteRow(writer, 4, new object?[] { "Reasons", "Human-readable evidence used by the planner, such as external ID match, normalized name similarity, domain match, phone match, postal code match, inactive target penalty, reviewer override, or missing authoritative integration target." });
        WriteRow(writer, 5, new object?[] { "Decision", "Accept Planned keeps the planned action. Create, Link, and Update explicitly approve that action. No Update skips without treating the row as rejected. Reject skips as rejected. Review leaves the row blocked from apply." });
        WriteRow(writer, 6, new object?[] { "Target Selection", "Changing the target name in the review sheet is treated as a reviewer override for Link/Update workflows. Ambiguous duplicate target names fail import instead of guessing." });
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static EntitySyncPlan ReadPlan(ZipArchive archive, IReadOnlyList<string> sharedStrings)
    {
        var entry = GetWorksheetEntry(archive, "_PlanJson") ?? throw new InvalidOperationException("Workbook does not contain the hidden plan data sheet.");
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        var encoded = new StringBuilder();
        foreach (var row in document.Descendants().Where(e => e.Name.LocalName == "row"))
        {
            var cell = row.Descendants().FirstOrDefault(e => e.Name.LocalName == "c");
            if (cell != null) encoded.Append(ReadCellText(cell, sharedStrings));
        }

        if (encoded.Length == 0) throw new InvalidOperationException("Workbook hidden plan data sheet is empty.");
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.ToString()));
        return JsonSerializer.Deserialize<EntitySyncPlan>(json) ?? throw new InvalidOperationException("Workbook hidden plan data did not contain a valid EntitySync plan.");
    }

    private static void ApplyReview(ZipArchive archive, EntitySyncPlan plan, IReadOnlyList<string> sharedStrings)
    {
        var entry = GetWorksheetEntry(archive, "Review") ?? throw new InvalidOperationException("Workbook does not contain a Review sheet.");
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        var rows = document.Descendants().Where(e => e.Name.LocalName == "row").ToList();
        if (rows.Count == 0) return;

        var headers = ReadRow(rows[0], sharedStrings);
        var itemColumn = FindColumn(headers, "Item");
        var decisionColumn = FindColumn(headers, "Decision");
        var targetNameColumn = FindColumn(headers, EntityNameHeader(plan.TargetVendor, plan.TargetEntityType));
        if (targetNameColumn < 0) targetNameColumn = FindColumn(headers, "TargetName");
        var notesColumn = FindColumn(headers, "ReviewerNotes");
        if (itemColumn < 0 || decisionColumn < 0) throw new InvalidOperationException("Review sheet must contain Item and Decision columns.");
        var targetsByName = TargetList(plan)
            .Select(target => new { Target = target, DisplayName = TargetDisplay(target).Trim() })
            .Where(target => !string.IsNullOrWhiteSpace(target.DisplayName))
            .GroupBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(target => target.Target).ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.Skip(1))
        {
            var cells = ReadRow(row, sharedStrings);
            if (!TryGetCell(cells, itemColumn, out var itemText) || !int.TryParse(itemText, out var itemNumber)) continue;
            if (itemNumber < 1 || itemNumber > plan.Items.Count) throw new InvalidOperationException($"Review sheet row references invalid item {itemNumber}.");

            var item = plan.Items[itemNumber - 1];
            var originalTargetName = item.Target == null ? null : TargetDisplay(item.Target);
            var hasDecision = TryGetCell(cells, decisionColumn, out var decision) && !string.IsNullOrWhiteSpace(decision);
            if (hasDecision) ApplyDecision(item, decision.Trim());
            if (targetNameColumn >= 0 && TryGetCell(cells, targetNameColumn, out var selectedTarget) && IsChangedTargetSelection(originalTargetName, selectedTarget))
            {
                if (item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase) || item.Action.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
                ApplyTargetSelection(item, selectedTarget.Trim(), targetsByName, itemNumber, hasDecision);
            }

            if (notesColumn >= 0 && TryGetCell(cells, notesColumn, out var notes) && !string.IsNullOrWhiteSpace(notes)) item.Reasons.Add("Reviewer note: " + notes.Trim());
        }

        ValidateDuplicateTargets(plan);
    }

    private static ZipArchiveEntry? GetWorksheetEntry(ZipArchive archive, string sheetName)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml");
        var relationshipsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (workbookEntry == null || relationshipsEntry == null) return null;

        using var workbookStream = workbookEntry.Open();
        using var relationshipsStream = relationshipsEntry.Open();
        var workbook = XDocument.Load(workbookStream);
        var relationships = XDocument.Load(relationshipsStream);
        var sheet = workbook.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "sheet" && string.Equals(element.Attribute("name")?.Value, sheetName, StringComparison.OrdinalIgnoreCase));
        var relationshipId = sheet?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value;
        if (string.IsNullOrWhiteSpace(relationshipId)) return null;

        var relationship = relationships.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Relationship" && string.Equals(element.Attribute("Id")?.Value, relationshipId, StringComparison.Ordinal));
        var target = relationship?.Attribute("Target")?.Value;
        if (string.IsNullOrWhiteSpace(target)) return null;

        return archive.GetEntry(NormalizeWorkbookTarget(target));
    }

    private static string NormalizeWorkbookTarget(string target)
    {
        var path = target.Replace('\\', '/');
        if (path.StartsWith('/')) path = path[1..];
        else path = "xl/" + path;

        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }

    private static void ValidateDuplicateTargets(EntitySyncPlan plan)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i];
            if (!ActionUsesTarget(item.Action) || string.IsNullOrWhiteSpace(item.Target?.Id)) continue;
            if (seen.TryGetValue(item.Target.Id, out var firstRow))
            {
                throw new InvalidOperationException($"Review items {firstRow - 1} and {i + 1} both use target '{item.Target.Id} {item.Target.Name}'. A target can only be selected for one source unless other items are Reject, No Update, Create, or Review.");
            }

            seen[item.Target.Id] = i + 2;
        }
    }

    private static bool ActionUsesTarget(string action)
    {
        return action.Equals("Link", StringComparison.OrdinalIgnoreCase) || action.Equals("Update", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyTargetSelection(EntitySyncPlanItem item, string selectedTarget, IReadOnlyDictionary<string, List<ExternalEntity>> targetsByName, int itemNumber, bool hasDecision)
    {
        if (!targetsByName.TryGetValue(selectedTarget, out var matches) || matches.Count == 0) throw new InvalidOperationException($"Review row {itemNumber} selected unknown TargetName '{selectedTarget}'. Choose a value from the dropdown.");
        if (matches.Count > 1) throw new InvalidOperationException($"Review row {itemNumber} selected ambiguous TargetName '{selectedTarget}'. Multiple targets have that name, so a name-only dropdown cannot safely identify the intended target.");
        var target = matches[0];
        if (item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase) || item.Action.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Review row {itemNumber} cannot combine a selected TargetName with action '{item.Action}'. Choose Link, Update, or Review.");
        }

        item.Target = target;
        if (!hasDecision || item.Action.Equals("Review", StringComparison.OrdinalIgnoreCase)) item.Action = "Link";
        item.Status = "Accepted";
        item.MatchType = "ReviewerOverride";
        item.Score = 100;
        item.Reasons.Add($"Reviewer selected target: {target.Id} {target.Name}".TrimEnd());
    }

    private static bool IsChangedTargetSelection(string? originalTargetName, string selectedTarget)
    {
        if (string.IsNullOrWhiteSpace(selectedTarget)) return false;
        return !string.Equals(originalTargetName?.Trim(), selectedTarget.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyDecision(EntitySyncPlanItem item, string decision)
    {
        if (decision.Equals("Accept Planned", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "Accepted";
            return;
        }

        if (decision.Equals("Reject", StringComparison.OrdinalIgnoreCase) || decision.Equals("No Update", StringComparison.OrdinalIgnoreCase))
        {
            item.Action = "None";
            item.Target = null;
            item.Status = decision.Equals("Reject", StringComparison.OrdinalIgnoreCase) ? "Rejected" : "NoUpdate";
            return;
        }

        if (decision.Equals("Create", StringComparison.OrdinalIgnoreCase) || decision.Equals("Link", StringComparison.OrdinalIgnoreCase) || decision.Equals("Update", StringComparison.OrdinalIgnoreCase))
        {
            item.Action = DecisionOptions.First(x => x.Equals(decision, StringComparison.OrdinalIgnoreCase));
            if (item.Action.Equals("Create", StringComparison.OrdinalIgnoreCase)) item.Target = null;
            item.Status = "Accepted";
            return;
        }

        if (decision.Equals("Review", StringComparison.OrdinalIgnoreCase))
        {
            item.Action = "Review";
            item.Status = "Review";
            return;
        }

        throw new InvalidOperationException($"Unsupported review decision '{decision}'. Use the workbook dropdown values.");
    }

    private static List<ExternalEntity> TargetList(EntitySyncPlan plan)
    {
        var targets = plan.TargetCandidates.Count > 0
            ? plan.TargetCandidates
            : plan.Items.Select(item => item.Target).Where(target => target != null).Select(target => target!).ToList();
        return targets
            .Where(target => !string.IsNullOrWhiteSpace(target.Id))
            .GroupBy(target => target.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string TargetDisplay(ExternalEntity target) => string.IsNullOrWhiteSpace(target.Name) ? target.Id : target.Name;

    private static string? SourceClientName(ExternalEntity source) => source.GetCustomField("HaloPsaClientName") ?? source.GetCustomField("HaloPsaClient");

    private static string? TargetCustomerName(EntitySyncPlanItem item)
    {
        return item.Target?.GetCustomField("NCentralCustomerName")
            ?? item.Target?.GetCustomField("NCentralCustomer")
            ?? item.Target?.GetCustomField("CustomerName")
            ?? item.Target?.GetCustomField("ParentCustomerName")
            ?? item.Source.GetCustomField("NCentralCustomerName")
            ?? item.Source.GetCustomField("NCentralCustomer");
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return Array.Empty<string>();

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        return document.Descendants()
            .Where(element => element.Name.LocalName == "si")
            .Select(item => string.Concat(item.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value)))
            .ToArray();
    }

    private static Dictionary<int, string> ReadRow(XElement row, IReadOnlyList<string> sharedStrings)
    {
        var values = new Dictionary<int, string>();
        foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "c"))
        {
            var reference = cell.Attribute("r")?.Value;
            var column = string.IsNullOrWhiteSpace(reference) ? values.Count : ColumnIndex(reference);
            values[column] = ReadCellText(cell, sharedStrings);
        }

        return values;
    }

    private static string ReadCellText(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var inlineText = cell.Descendants().FirstOrDefault(e => e.Name.LocalName == "t")?.Value;
        if (inlineText != null) return inlineText;
        var value = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Empty;
        if (cell.Attribute("t")?.Value == "s" && int.TryParse(value, out var sharedStringIndex) && sharedStringIndex >= 0 && sharedStringIndex < sharedStrings.Count) return sharedStrings[sharedStringIndex];
        return value;
    }

    private static int FindColumn(Dictionary<int, string> headers, string name)
    {
        foreach (var header in headers)
        {
            if (header.Value.Equals(name, StringComparison.OrdinalIgnoreCase)) return header.Key;
        }

        return -1;
    }

    private static bool TryGetCell(Dictionary<int, string> cells, int column, out string value) => cells.TryGetValue(column, out value!);

    private static int ColumnIndex(string cellReference)
    {
        var index = 0;
        foreach (var ch in cellReference)
        {
            if (!char.IsLetter(ch)) break;
            index = index * 26 + char.ToUpperInvariant(ch) - 'A' + 1;
        }

        return index - 1;
    }

    private static void WriteRow(XmlWriter writer, int rowIndex, object?[] values, int? styleIndex = 0)
    {
        writer.WriteStartElement("row");
        writer.WriteAttributeString("r", rowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteAttributeString("ht", "17");
        writer.WriteAttributeString("customHeight", "1");
        for (var i = 0; i < values.Length; i++) WriteCell(writer, rowIndex, i, values[i], styleIndex);
        writer.WriteEndElement();
    }

    private static void WriteColumn(XmlWriter writer, int min, int max, int width)
    {
        writer.WriteStartElement("col");
        writer.WriteAttributeString("min", min.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteAttributeString("max", max.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteAttributeString("width", width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteAttributeString("customWidth", "1");
        writer.WriteEndElement();
    }

    private static void WriteReviewColumns(XmlWriter writer, IReadOnlyList<string> headers)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            WriteColumn(writer, i + 1, i + 1, ColumnWidth(headers[i]));
        }
    }

    private static int ColumnWidth(string header)
    {
        if (header.Equals("Item", StringComparison.OrdinalIgnoreCase)) return 8;
        if (header.Equals("Decision", StringComparison.OrdinalIgnoreCase)) return 18;
        if (header.Equals("PlannedAction", StringComparison.OrdinalIgnoreCase) || header.Equals("Status", StringComparison.OrdinalIgnoreCase)) return 14;
        if (header.EndsWith("Name", StringComparison.OrdinalIgnoreCase)) return 38;
        if (header.Equals("Score", StringComparison.OrdinalIgnoreCase) || header.Equals("MatchType", StringComparison.OrdinalIgnoreCase)) return 12;
        if (header.Equals("Reasons", StringComparison.OrdinalIgnoreCase)) return 60;
        if (header.Equals("ReviewerNotes", StringComparison.OrdinalIgnoreCase)) return 50;
        return 18;
    }

    private static void WriteCell(XmlWriter writer, int rowIndex, int columnIndex, object? value, int? styleIndex)
    {
        writer.WriteStartElement("c");
        writer.WriteAttributeString("r", CellReference(rowIndex, columnIndex));
        if (styleIndex.HasValue) writer.WriteAttributeString("s", styleIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (value is int integer)
        {
            writer.WriteElementString("v", integer.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is");
            writer.WriteElementString("t", value?.ToString() ?? string.Empty);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static string CellReference(int rowIndex, int columnIndex) => ColumnName(columnIndex) + rowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string ColumnName(int columnIndex)
    {
        var dividend = columnIndex + 1;
        var name = string.Empty;
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            name = Convert.ToChar('A' + modulo) + name;
            dividend = (dividend - modulo) / 26;
        }

        return name;
    }

    private static IEnumerable<string> Chunk(string value, int size)
    {
        for (var i = 0; i < value.Length; i += size) yield return value.Substring(i, Math.Min(size, value.Length - i));
    }

    private static void WriteText(ZipArchive archive, string path, string text)
    {
        using var stream = archive.CreateEntry(path).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string ContentTypesXml() => """
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet3.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet4.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet5.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
          <Override PartName="/xl/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/>
          <Override PartName="/xl/tables/table1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.table+xml"/>
        </Types>
        """;

    private static string RootRelationshipsXml() => """
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    private static string ReviewSheetRelationshipsXml() => """
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/table" Target="../tables/table1.xml"/>
        </Relationships>
        """;

    private static string WorkbookRelationshipsXml() => """
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
          <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet3.xml"/>
          <Relationship Id="rId4" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet4.xml"/>
          <Relationship Id="rId5" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet5.xml"/>
          <Relationship Id="rId6" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
          <Relationship Id="rId7" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme" Target="theme/theme1.xml"/>
        </Relationships>
        """;

    private static string WorkbookXml() => """
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets>
            <sheet name="Review" sheetId="1" r:id="rId1"/>
            <sheet name="Legend" sheetId="5" r:id="rId5"/>
            <sheet name="_PlanJson" sheetId="2" state="veryHidden" r:id="rId2"/>
            <sheet name="_Lists" sheetId="3" state="veryHidden" r:id="rId3"/>
            <sheet name="_Targets" sheetId="4" state="veryHidden" r:id="rId4"/>
          </sheets>
        </workbook>
        """;

    private static string StylesXml() => """
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="2"><font><sz val="11"/><name val="Aptos Narrow"/></font><font><b/><sz val="11"/><name val="Aptos Display"/></font></fonts>
          <fills count="1"><fill><patternFill patternType="none"/></fill></fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="center" indent="1"/></xf><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment vertical="center" indent="1"/></xf></cellXfs>
          <dxfs count="1"><dxf><font><color rgb="FF9C5700"/></font><fill><patternFill patternType="solid"><fgColor rgb="FFFFEB9C"/><bgColor rgb="FFFFEB9C"/></patternFill></fill></dxf></dxfs>
        </styleSheet>
        """;

    private static string ThemeXml() => """
        <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Integral">
          <a:themeElements>
            <a:clrScheme name="Integral"><a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1><a:dk2><a:srgbClr val="1F1F1F"/></a:dk2><a:lt2><a:srgbClr val="F2F2F2"/></a:lt2><a:accent1><a:srgbClr val="4F81BD"/></a:accent1><a:accent2><a:srgbClr val="C0504D"/></a:accent2><a:accent3><a:srgbClr val="9BBB59"/></a:accent3><a:accent4><a:srgbClr val="8064A2"/></a:accent4><a:accent5><a:srgbClr val="4BACC6"/></a:accent5><a:accent6><a:srgbClr val="F79646"/></a:accent6><a:hlink><a:srgbClr val="0000FF"/></a:hlink><a:folHlink><a:srgbClr val="800080"/></a:folHlink></a:clrScheme>
            <a:fontScheme name="Integral"><a:majorFont><a:latin typeface="Aptos Display"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont><a:minorFont><a:latin typeface="Aptos Narrow"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme>
            <a:fmtScheme name="Integral"><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:gradFill rotWithShape="1"><a:gsLst><a:gs pos="0"><a:schemeClr val="phClr"><a:lumMod val="110000"/><a:satMod val="105000"/><a:tint val="67000"/></a:schemeClr></a:gs><a:gs pos="50000"><a:schemeClr val="phClr"><a:lumMod val="105000"/><a:satMod val="103000"/><a:tint val="73000"/></a:schemeClr></a:gs><a:gs pos="100000"><a:schemeClr val="phClr"><a:lumMod val="105000"/><a:satMod val="109000"/><a:tint val="81000"/></a:schemeClr></a:gs></a:gsLst><a:lin ang="5400000" scaled="0"/></a:gradFill><a:gradFill rotWithShape="1"><a:gsLst><a:gs pos="0"><a:schemeClr val="phClr"><a:satMod val="103000"/><a:lumMod val="102000"/><a:tint val="94000"/></a:schemeClr></a:gs><a:gs pos="50000"><a:schemeClr val="phClr"><a:satMod val="110000"/><a:lumMod val="100000"/><a:shade val="100000"/></a:schemeClr></a:gs><a:gs pos="100000"><a:schemeClr val="phClr"><a:lumMod val="99000"/><a:satMod val="120000"/><a:shade val="78000"/></a:schemeClr></a:gs></a:gsLst><a:lin ang="5400000" scaled="0"/></a:gradFill></a:fillStyleLst><a:lnStyleLst><a:ln w="6350" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/></a:ln><a:ln w="12700" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/></a:ln><a:ln w="19050" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/></a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"><a:tint val="95000"/><a:satMod val="170000"/></a:schemeClr></a:solidFill><a:gradFill rotWithShape="1"><a:gsLst><a:gs pos="0"><a:schemeClr val="phClr"><a:tint val="93000"/><a:satMod val="150000"/><a:shade val="98000"/><a:lumMod val="102000"/></a:schemeClr></a:gs><a:gs pos="50000"><a:schemeClr val="phClr"><a:tint val="98000"/><a:satMod val="130000"/><a:shade val="90000"/><a:lumMod val="103000"/></a:schemeClr></a:gs><a:gs pos="100000"><a:schemeClr val="phClr"><a:shade val="63000"/><a:satMod val="120000"/></a:schemeClr></a:gs></a:gsLst><a:lin ang="5400000" scaled="0"/></a:gradFill></a:bgFillStyleLst></a:fmtScheme>
          </a:themeElements>
        </a:theme>
        """;

    private static string ReviewTableXml(EntitySyncPlan plan)
    {
        var headers = HeadersForPlan(plan);
        var lastRow = Math.Max(1, plan.Items.Count + 1);
        var lastColumn = ColumnName(headers.Length - 1);
        var columns = string.Join(string.Empty, headers.Select((header, index) => $"<tableColumn id=\"{index + 1}\" name=\"{SecurityElementEscape(header)}\"/>"));
        return $$"""
            <table xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" id="1" name="EntitySyncReview" displayName="EntitySyncReview" ref="A1:{{lastColumn}}{{lastRow}}" totalsRowShown="0">
              <autoFilter ref="A1:{{lastColumn}}{{lastRow}}"/>
              <tableColumns count="{{headers.Length}}">{{columns}}</tableColumns>
              <tableStyleInfo name="TableStyleMedium12" showFirstColumn="0" showLastColumn="0" showRowStripes="1" showColumnStripes="0"/>
            </table>
            """;
    }

    private static string SecurityElementEscape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
}
