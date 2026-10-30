namespace TempFanWidget.Cards;

/// <summary>
/// Adaptive Card templates, one per widget size (Small / Medium / Large).
/// Values come from <see cref="CardViewModel"/> through <c>${...}</c>
/// placeholders which the widget host fills at render time.
/// </summary>
public static class CardTemplates
{
    /// <summary>Small (2×1). Two temperature tiles, then one line for load and network speed.</summary>
    public const string Small = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.6",
          "body": [
            {
              "type": "ColumnSet",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "style": "emphasis",
                  "items": [
                    { "type": "TextBlock", "text": "CPU${cpuNote}", "size": "Small", "weight": "Bolder", "isSubtle": true },
                    { "type": "TextBlock", "text": "${cpuTempDisplay}", "size": "ExtraLarge", "weight": "Bolder", "color": "${cpuTempColor}", "spacing": "None" }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "style": "emphasis",
                  "items": [
                    { "type": "TextBlock", "text": "GPU", "size": "Small", "weight": "Bolder", "isSubtle": true },
                    { "type": "TextBlock", "text": "${gpuTempDisplay}", "size": "ExtraLarge", "weight": "Bolder", "color": "${gpuTempColor}", "spacing": "None" }
                  ]
                }
              ]
            },
            {
              "type": "TextBlock",
              "text": "Load ${cpuLoadDisplay}  ·  ↓ ${downloadDisplay}  ↑ ${uploadDisplay}",
              "size": "Small",
              "isSubtle": true,
              "spacing": "Small"
            }
          ]
        }
        """;

    /// <summary>Medium (2×2). Dashboard with logo header, side-by-side metric tiles, network row, footer & refresh action.</summary>
    public const string Medium = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.6",
          "body": [
            {
              "type": "ColumnSet",
              "columns": [
                {
                  "type": "Column",
                  "width": "auto",
                  "verticalContentAlignment": "Center",
                  "$when": "${logoDataUri != ''}",
                  "items": [
                    { "type": "Image", "url": "${logoDataUri}", "width": "28px", "height": "28px", "altText": "W-INFO logo" }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "spacing": "Small",
                  "verticalContentAlignment": "Center",
                  "items": [
                    { "type": "TextBlock", "text": "W-INFO", "weight": "Bolder", "size": "Medium" }
                  ]
                },
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    { "type": "TextBlock", "text": "CPU Load: ${cpuLoadDisplay}", "size": "Small", "weight": "Bolder", "isSubtle": true, "horizontalAlignment": "Right" }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "Medium",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "style": "emphasis",
                  "items": [
                    { "type": "TextBlock", "text": "CPU${cpuNote}", "size": "Small", "weight": "Bolder", "isSubtle": true },
                    { "type": "TextBlock", "text": "${cpuTempDisplay}", "size": "ExtraLarge", "weight": "Bolder", "color": "${cpuTempColor}", "spacing": "None" },
                    { "type": "TextBlock", "text": "Fan: ${cpuFanDisplay}", "size": "Small", "spacing": "Small" }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "style": "emphasis",
                  "items": [
                    { "type": "TextBlock", "text": "GPU THERMALS", "size": "Small", "weight": "Bolder", "isSubtle": true },
                    { "type": "TextBlock", "text": "${gpuTempDisplay}", "size": "ExtraLarge", "weight": "Bolder", "color": "${gpuTempColor}", "spacing": "None" },
                    { "type": "TextBlock", "text": "Fan: ${gpuFanDisplay}", "size": "Small", "spacing": "Small" }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "Small",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "style": "emphasis",
                  "items": [
                    { "type": "TextBlock", "text": "↓ ${downloadDisplay}", "size": "Medium", "weight": "Bolder" }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "style": "emphasis",
                  "items": [
                    { "type": "TextBlock", "text": "↑ ${uploadDisplay}", "size": "Medium", "weight": "Bolder" }
                  ]
                }
              ]
            },
            {
              "type": "TextBlock",
              "text": "● ${sourceDisplay}  ·  ${updatedDisplay}",
              "size": "Small",
              "isSubtle": true,
              "spacing": "Medium"
            }
          ],
          "actions": [
            {
              "type": "Action.Execute",
              "title": "↻ Refresh",
              "verb": "refresh"
            }
          ]
        }
        """;

    /// <summary>Large (4×2). Detailed overview: CPU, GPU and network side by side, plus telemetry source.</summary>
    public const string Large = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.6",
          "body": [
            {
              "type": "ColumnSet",
              "columns": [
                {
                  "type": "Column",
                  "width": "auto",
                  "verticalContentAlignment": "Center",
                  "$when": "${logoDataUri != ''}",
                  "items": [
                    { "type": "Image", "url": "${logoDataUri}", "width": "28px", "height": "28px", "altText": "W-INFO logo" }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "spacing": "Small",
                  "verticalContentAlignment": "Center",
                  "items": [
                    { "type": "TextBlock", "text": "W-INFO TELEMETRY", "weight": "Bolder", "size": "Medium" }
                  ]
                },
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    { "type": "TextBlock", "text": "Updated: ${updatedDisplay}", "size": "Small", "isSubtle": true, "horizontalAlignment": "Right" }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "Medium",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "style": "emphasis",
                  "items": [
                    { "type": "TextBlock", "text": "CPU${cpuNote}", "size": "Small", "weight": "Bolder", "isSubtle": true },
                    { "type": "TextBlock", "text": "${cpuTempDisplay}", "size": "ExtraLarge", "weight": "Bolder", "color": "${cpuTempColor}", "spacing": "None" },
                    { "type": "TextBlock", "text": "Load: ${cpuLoadDisplay}", "size": "Small", "spacing": "Small" },
                    { "type": "TextBlock", "text": "Fan: ${cpuFanDisplay}", "size": "Small", "isSubtle": true, "spacing": "None" }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "style": "emphasis",
                  "items": [
                    { "type": "TextBlock", "text": "GPU THERMALS", "size": "Small", "weight": "Bolder", "isSubtle": true },
                    { "type": "TextBlock", "text": "${gpuTempDisplay}", "size": "ExtraLarge", "weight": "Bolder", "color": "${gpuTempColor}", "spacing": "None" },
                    { "type": "TextBlock", "text": "Fan: ${gpuFanDisplay}", "size": "Small", "spacing": "Small" }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "style": "emphasis",
                  "items": [
                    { "type": "TextBlock", "text": "NETWORK", "size": "Small", "weight": "Bolder", "isSubtle": true },
                    { "type": "TextBlock", "text": "↓ ${downloadDisplay}", "size": "Large", "weight": "Bolder", "spacing": "None" },
                    { "type": "TextBlock", "text": "↑ ${uploadDisplay}", "size": "Large", "weight": "Bolder", "spacing": "Small" }
                  ]
                }
              ]
            },
            {
              "type": "TextBlock",
              "text": "Telemetry Source: ${sourceDisplay}",
              "size": "Small",
              "isSubtle": true,
              "spacing": "Medium"
            }
          ],
          "actions": [
            {
              "type": "Action.Execute",
              "title": "↻ Refresh Telemetry",
              "verb": "refresh"
            },
            {
              "type": "Action.OpenUrl",
              "title": "Accurate CPU temp",
              "url": "https://github.com/maczeo11/W-INFO#accurate-cpu-temperature-and-fans",
              "$when": "${cpuNote != ''}"
            }
          ]
        }
        """;
}
