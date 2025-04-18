﻿using Newtonsoft.Json.Linq;
using Progress.Sitefinity.MigrationTool.Core.Widgets;
using Progress.Sitefinity.RestSdk;
using Progress.Sitefinity.RestSdk.Filters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Reflection.Metadata;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Progress.Sitefinity.MigrationTool.ConsoleApp.Migrations.Mvc;
internal class DocumentListWidget : ContentWidget
{
    protected override string RendererWidgetName { get { return "SitefinityDocumentList";  } }

    protected override async Task MigrateViews(WidgetMigrationContext context, Dictionary<string, string> propsToRead, IDictionary<string, string> migratedProperties, string contentType)
    {
        await context.LogWarning($"Defaulting to view DocumentList for content type {contentType}");
        propsToRead.TryGetValue("ListTemplateName", out string listViewName);

        string rendererListViewName;
        switch (listViewName)
        {
            case "DocumentsList":
                rendererListViewName = "DocumentList";
                break;
            case "DocumentsTable":
                rendererListViewName = "DocumentTable";
                break;
            default:
                rendererListViewName = "DocumentList";
                break;
        }
        migratedProperties.Add("SfViewName", rendererListViewName);

        migratedProperties.Add("SfDetailViewName", "Details.DocumentDetails");
    }
}
