using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Persistence;

public sealed partial class SqliteDesktopStore : ITemplateRepository, IBatchSnapshotRepository, ICalibrationStateRepository, ITemplatePreviewRepository
{
}
