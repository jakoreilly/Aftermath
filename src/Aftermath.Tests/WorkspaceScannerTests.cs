namespace Aftermath.Tests;

using Aftermath.Discovery;

/// <summary>
/// Fixture content is copied VERBATIM from the real estate so these tests fail if the
/// house conventions move. Sources are named on each constant.
/// </summary>
public class WorkspaceScannerTests
{
    // c:\workspace\work\billing-service\.gitlab-ci.yml:1-54 (abridged to the lines that matter)
    private const string BillingCi =
        """
        include:
          - project: 'acme-org/acme-images/semantic-release'
            ref: master
            file:
              - '/jobs_v2.yml'
              - '/jobs_dotnet_v3.yml'

        variables:
          dotnetcore_image: $DOCKER_REGISTRY/acme-dotnet:9.0
          NUGET_PACKAGES_DIRECTORY: 'nuget_packages'
          od_project_slug: ledger-billing-service
          solution_file: src/Acme.Ledger.BillingService.sln

        .package-vars: &package-vars
          package_base_path: dist/webapi
          package_name: Acme.Ledger.BillingService.WebApi
          output_directory: dist/nuget

        check-od-vars:
          extends: .check-od-vars
          variables:
            od_project: $od_project_slug
            settings_file: src/Acme.Ledger.BillingService.WebApi/appsettings.json
        """;

    // c:\workspace\work\shared\.gitlab-ci.yml — a NuGet library: no od_project_slug at all.
    private const string SharedLibraryCi =
        """
        variables:
          dotnetcore_image: $DOCKER_REGISTRY/acme-dotnet:9.0
          VERSION: 9.7.0
        stages:
          - build
        """;

    // c:\workspace\work\accountservice\src\...\log4net.config:1-29
    private const string AccountServiceLog4Net =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <log4net>
          <appender name="ConsoleAppender" type="log4net.Appender.ConsoleAppender">
            <layout type="log4net.Layout.PatternLayout">
              <conversionPattern value="%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [%4thread] %-5level %logger traceId: %X{trace_id} spanId: %X{span_id} - %message%newline"/>
            </layout>
          </appender>
          <appender name="FileAppender" type="log4net.Appender.RollingFileAppender">
            <file value="logs\Acme.Ledger.AccountService.WebApi_"/>
            <appendToFile value="true"/>
            <rollingStyle value="Composite"/>
            <datePattern value="yyyy-MM-dd.\l\o\g"/>
            <maximumFileSize value="100MB"/>
            <staticLogFileName value="false"/>
            <layout type="log4net.Layout.PatternLayout">
              <conversionPattern value="%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [%4thread] %-5level %logger traceId: %X{trace_id} spanId: %X{span_id} - %message%newline"/>
            </layout>
          </appender>
          <root>
            <level value="ALL" />
            <appender-ref ref="FileAppender" />
            <appender-ref ref="ConsoleAppender" />
          </root>
        </log4net>
        """;

    // c:\workspace\work\accountservice\src\...\log4net.Release.config:1-32
    private const string AccountServiceLog4NetRelease =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <log4net  xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
          <appender xdt:Transform="RemoveAll" />
          <appender name="FileAppender" type="log4net.Appender.RollingFileAppender" xdt:Transform="Insert">
            <file value="#{Acme.Logs.Path.And.File.Prefix}"/>
            <datePattern value="yyyy-MM-dd.\l\o\g"/>
            <layout type="log4net.Layout.PatternLayout">
              <conversionPattern value="%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [%4thread] %-5level %logger traceId: %X{trace_id} spanId: %X{span_id} - %message%newline"/>
            </layout>
          </appender>
          <appender name="RemoteSyslogAppender" type="log4net.Appender.RemoteSyslogAppender" xdt:Transform="Insert">
            <identity value="Acme.Ledger.AccountService.WebApi"></identity>
            <remoteAddress value="#{Acme.Logs.Central.Server.SysLog.Address}" />
            <remotePort value="#{Acme.Logs.Central.Server.SysLog.Port}" />
          </appender>
          <root>
            <level value="#{Logging.Level}" />
          </root>
        </log4net>
        """;

    // Verbatim: c:\workspace\work\api-gateway2\src\Acme.Ledger.ApiGateway2\log4net.Release.config:1-34
    private const string ApiGateway2Log4NetRelease =
        """
        <?xml version="1.0" encoding="utf-8"?>

        <log4net xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
          <appender xdt:Transform="RemoveAll"/>

          <appender name="FileAppender" type="log4net.Appender.RollingFileAppender" xdt:Transform="Insert">
            <file value="#{Acme.Logs.Path.And.File.Prefix}"/>
            <appendToFile value="true"/>
            <rollingStyle value="Composite"/>
            <datePattern value="yyyy-MM-dd.\l\o\g"/>
            <maximumFileSize value="100MB"/>
            <staticLogFileName value="false"/>
            <layout type="log4net.Layout.PatternLayout">
              <conversionPattern
                value="%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [%4thread] %-5level %logger traceId: %X{trace_id} spanId: %X{span_id} - %message"/>
            </layout>
          </appender>

          <appender name="RemoteSyslogAppender" type="log4net.Appender.RemoteSyslogAppender" xdt:Transform="Insert">
            <layout type="log4net.Layout.PatternLayout"
                    value="%date{HH:mm:ss.fff} [%4thread] [%property{AcmeLogPrefix}] %-5level %logger traceId: %X{trace_id} spanId: %X{span_id} - %message"/>
            <identity value="Acme.Ledger.ApiGateway2"></identity>
            <remoteAddress value="#{Acme.Logs.Central.Server.SysLog.Address}"/>
            <remotePort value="#{Acme.Logs.Central.Server.SysLog.Port}"/>
          </appender>

          <root>
            <level value="#{Logging.Level}"/>
            <appender-ref xdt:Transform="RemoveAll"/>
            <appender-ref ref="FileAppender" xdt:Transform="Insert"/>
            <appender-ref ref="RemoteSyslogAppender" xdt:Transform="Insert"/>
          </root>

        </log4net>
        """;

    // c:\workspace\work\accountservice\src\...\appsettings.json:204-208
    private const string AccountServiceAppSettings =
        """
        {
          "ConnectionStrings": { "LedgerDatabaseContext": "Server=x;" },
          "OpenTelemetrySettings": {
            "Enable": true,
            "ServiceName": "lg_accountsvc",
            "UseTailSampler": "false"
          }
        }
        """;

    [Fact]
    public void ReadOctopusSlug_FindsTheSlug()
        => Assert.Equal("ledger-billing-service", WorkspaceScanner.ReadOctopusSlug(BillingCi));

    [Fact]
    public void ReadOctopusSlug_ReturnsNull_ForANuGetLibraryWithNoOctopusProject()
        => Assert.Null(WorkspaceScanner.ReadOctopusSlug(SharedLibraryCi));

    [Fact]
    public void ReadOctopusSlug_DoesNotMatchTheReferenceToTheVariable()
    {
        // "od_project: $od_project_slug" must not be mistaken for the definition.
        string? slug = WorkspaceScanner.ReadOctopusSlug(BillingCi);
        Assert.DoesNotContain("$", slug);
    }

    [Fact]
    public void ReadPackageName_FindsTheDeployablePackage()
        => Assert.Equal("Acme.Ledger.BillingService.WebApi", WorkspaceScanner.ReadPackageName(BillingCi));

    [Fact]
    public void ReadLogFilePrefix_ReadsTheDevRelativePrefix()
        => Assert.Equal(@"logs\Acme.Ledger.AccountService.WebApi_", WorkspaceScanner.ReadLogFilePrefix(AccountServiceLog4Net));

    [Fact]
    public void ReadLogFilePrefix_ReadsTheOctopusTokenFromAReleaseConfig()
        => Assert.Equal("#{Acme.Logs.Path.And.File.Prefix}", WorkspaceScanner.ReadLogFilePrefix(AccountServiceLog4NetRelease));

    [Theory]
    [InlineData("#{Acme.Logs.Path.And.File.Prefix}", true)]
    [InlineData(@"logs\Acme.Ledger.AccountService.WebApi_", false)]
    [InlineData(null, false)]
    public void IsOctopusToken_DistinguishesATokenFromAPath(string? value, bool expected)
        => Assert.Equal(expected, WorkspaceScanner.IsOctopusToken(value));

    [Fact]
    public void ReadLogPatterns_DeduplicatesTheConsoleAndFileAppenderPatterns()
    {
        IReadOnlyList<string> patterns = WorkspaceScanner.ReadLogPatterns(AccountServiceLog4Net);

        // Both appenders carry the identical pattern, so one distinct value.
        Assert.Single(patterns);
        Assert.StartsWith("%date{HH:mm:ss.fff}", patterns[0]);
        Assert.Contains("%X{trace_id}", patterns[0]);
    }

    [Fact]
    public void ReadLogPatterns_FindsBothAppenderForms_AndTheirDifferingFieldOrder()
    {
        // Verbatim: c:\workspace\work\api-gateway2\src\...\log4net.Release.config:1-34.
        // Two things this file proves, both of which break a naive parser:
        //  (1) the syslog appender declares its pattern on the layout ELEMENT (line 20-21),
        //      not as a conversionPattern child, so it is missed entirely by the common form;
        //  (2) the same service writes DIFFERENT field orders to its two destinations —
        //      "[prefix] [thread]" to file, "[thread] [prefix]" to syslog.
        // Attributes are also split across lines, so the regexes must tolerate newlines.
        IReadOnlyList<string> patterns = WorkspaceScanner.ReadLogPatterns(ApiGateway2Log4NetRelease);

        Assert.Equal(2, patterns.Count);
        Assert.Contains(patterns, p => p.Contains("[%property{AcmeLogPrefix}] [%4thread]", StringComparison.Ordinal));
        Assert.Contains(patterns, p => p.Contains("[%4thread] [%property{AcmeLogPrefix}]", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadLogPatterns_DoesNotMistakeTheLayoutTypeAttributeForAPattern()
    {
        IReadOnlyList<string> patterns = WorkspaceScanner.ReadLogPatterns(
            """<layout type="log4net.Layout.PatternLayout" value="%message%newline" />""");

        Assert.Equal(["%message%newline"], patterns);
    }

    [Fact]
    public void ReadLogPatterns_CapturesBothCorrelationPropertyNames()
    {
        // The estate uses two names for the correlation prefix: AcmeLogPrefix (68 uses)
        // and SessionID (18). Both carry a session identifier and both must be redactable.
        IReadOnlyList<string> patterns = WorkspaceScanner.ReadLogPatterns(
            """
            <log4net>
              <appender name="A"><layout type="log4net.Layout.PatternLayout" value="%date{HH:mm:ss.fff} [%property{SessionID}] %-5level %logger - %message%newline" /></appender>
              <appender name="B"><layout type="log4net.Layout.PatternLayout"><conversionPattern value="%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] %-5level %logger - %message%newline"/></layout></appender>
            </log4net>
            """);

        Assert.Equal(2, patterns.Count);
        Assert.Contains(patterns, p => p.Contains("%property{SessionID}", StringComparison.Ordinal));
        Assert.Contains(patterns, p => p.Contains("%property{AcmeLogPrefix}", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadOtelServiceName_ReadsTheNestedValue()
        => Assert.Equal("lg_accountsvc", WorkspaceScanner.ReadOtelServiceName(AccountServiceAppSettings));

    [Fact]
    public void ReadOtelServiceName_ReturnsNull_WhenTheSectionIsAbsent()
    {
        // gdpr-service has no OpenTelemetrySettings section at all. A synthesised name would
        // silently match no traces while looking like it worked.
        Assert.Null(WorkspaceScanner.ReadOtelServiceName("""{ "Logging": { "LogLevel": {} } }"""));
    }

    [Fact]
    public void ReadOtelServiceName_ReturnsNull_ForMalformedJson()
        => Assert.Null(WorkspaceScanner.ReadOtelServiceName("{ not json"));

    [Fact]
    public void ReadOtelServiceName_ToleratesCommentsAndTrailingCommas()
        => Assert.Equal(
            "ACME_lg_zonesvc",
            WorkspaceScanner.ReadOtelServiceName("""
            {
              // zoneservice uses a different prefix — the name is not derivable, only readable
              "OpenTelemetrySettings": { "ServiceName": "ACME_lg_zonesvc", }
            }
            """));

    [Fact]
    public void Extractors_ReturnNull_OnEmptyInput()
    {
        Assert.Null(WorkspaceScanner.ReadOctopusSlug(string.Empty));
        Assert.Null(WorkspaceScanner.ReadPackageName(string.Empty));
        Assert.Null(WorkspaceScanner.ReadLogFilePrefix(string.Empty));
        Assert.Empty(WorkspaceScanner.ReadLogPatterns(string.Empty));
    }
}
