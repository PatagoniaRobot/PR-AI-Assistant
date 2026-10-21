using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PRImager.AIAssistant.Helpers;

/// <summary>
/// Convierte texto Markdown en FlowDocument para renderizado en burbujas del chat.
/// Soporta: code blocks, headers, listas, bold, italic, code inline, separadores.
/// Sin dependencias externas — parser basado en Regex.
/// </summary>
public static class MarkdownParser
{
    private static readonly SolidColorBrush s_fgNormal = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0E0E0"));
    private static readonly SolidColorBrush s_fgCode = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
    private static readonly SolidColorBrush s_bgCodeBlock = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F172A"));
    private static readonly SolidColorBrush s_borderCodeBlock = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155"));
    private static readonly SolidColorBrush s_fgLang = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6B7280"));
    
    private static readonly SolidColorBrush s_fgH1 = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00D4AA"));
    private static readonly SolidColorBrush s_fgH2 = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
    private static readonly SolidColorBrush s_fgH3 = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));

    private static readonly FontFamily s_consolas = new FontFamily("Consolas");

    /// <summary>
    /// Parsea el contenido Markdown y genera un FlowDocument estilizado para el tema oscuro.
    /// </summary>
    public static FlowDocument Parse(string markdown)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = s_consolas,
            FontSize = 12,
            Foreground = s_fgNormal,
            Background = Brushes.Transparent
        };

        if (string.IsNullOrEmpty(markdown)) return doc;

        try
        {
            string codeBlockPattern = @"```(.*?)\n(.*?)```";
            var codeBlocks = Regex.Matches(markdown, codeBlockPattern, RegexOptions.Singleline);
            
            int currentIndex = 0;
            foreach (Match match in codeBlocks)
            {
                if (match.Index > currentIndex)
                {
                    string textBefore = markdown.Substring(currentIndex, match.Index - currentIndex);
                    ProcessBlocks(doc, textBefore);
                }

                string lang = match.Groups[1].Value.Trim();
                string code = match.Groups[2].Value;
                if (code.EndsWith("\n")) code = code.Substring(0, code.Length - 1);
                if (code.EndsWith("\r")) code = code.Substring(0, code.Length - 1);

                doc.Blocks.Add(CreateCodeBlock(lang, code));

                currentIndex = match.Index + match.Length;
            }

            if (currentIndex < markdown.Length)
            {
                string textAfter = markdown.Substring(currentIndex);
                ProcessBlocks(doc, textAfter);
            }
        }
        catch (Exception)
        {
            doc.Blocks.Clear();
            doc.Blocks.Add(new Paragraph(new Run(markdown)));
        }

        return doc;
    }

    private static Block CreateCodeBlock(string lang, string code)
    {
        var section = new Section();
        if (!string.IsNullOrEmpty(lang))
        {
            var pLang = new Paragraph(new Run(lang) { Foreground = s_fgLang, FontSize = 9 }) { Margin = new Thickness(0, 0, 0, 2) };
            section.Blocks.Add(pLang);
        }

        var pCode = new Paragraph(new Run(code))
        {
            FontFamily = s_consolas,
            FontSize = 11,
            Foreground = s_fgNormal,
            Background = s_bgCodeBlock,
            BorderBrush = s_borderCodeBlock,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8)
        };
        section.Blocks.Add(pCode);

        return section;
    }

    private static void ProcessBlocks(FlowDocument doc, string text)
    {
        string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        
        List<string> currentBlockLines = new List<string>();
        string currentBlockType = "Paragraph";

        Action flushBlock = () =>
        {
            if (currentBlockLines.Count == 0) return;

            string blockContent = string.Join("\n", currentBlockLines);

            if (currentBlockType == "Separator")
            {
                var border = new Border
                {
                    Height = 1,
                    Background = s_borderCodeBlock,
                    Margin = new Thickness(0, 8, 0, 8)
                };
                doc.Blocks.Add(new BlockUIContainer(border));
            }
            else if (currentBlockType == "UnorderedList" || currentBlockType == "OrderedList")
            {
                doc.Blocks.Add(CreateList(currentBlockLines, currentBlockType == "OrderedList"));
            }
            else
            {
                if (blockContent.StartsWith("#"))
                {
                    var match = Regex.Match(blockContent, @"^(#{1,3})\s+(.*)$");
                    if (match.Success)
                    {
                        int level = match.Groups[1].Value.Length;
                        string headingText = match.Groups[2].Value;
                        doc.Blocks.Add(CreateHeader(level, headingText));
                        currentBlockLines.Clear();
                        return;
                    }
                }
                
                doc.Blocks.Add(CreateParagraph(blockContent));
            }

            currentBlockLines.Clear();
        };

        foreach (string line in lines)
        {
            string tLine = line.Trim();

            if (string.IsNullOrEmpty(tLine))
            {
                flushBlock();
                currentBlockType = "Paragraph";
                continue;
            }

            if (tLine == "---" || tLine == "***")
            {
                flushBlock();
                currentBlockType = "Separator";
                currentBlockLines.Add(tLine);
                flushBlock();
                currentBlockType = "Paragraph";
                continue;
            }

            bool isUnordered = tLine.StartsWith("- ") || tLine.StartsWith("* ");
            bool isOrdered = Regex.IsMatch(tLine, @"^\d+\.\s+");

            if (isUnordered)
            {
                if (currentBlockType != "UnorderedList") flushBlock();
                currentBlockType = "UnorderedList";
                currentBlockLines.Add(line);
            }
            else if (isOrdered)
            {
                if (currentBlockType != "OrderedList") flushBlock();
                currentBlockType = "OrderedList";
                currentBlockLines.Add(line);
            }
            else
            {
                if (currentBlockType == "UnorderedList" || currentBlockType == "OrderedList")
                {
                    currentBlockLines.Add(line);
                }
                else
                {
                    if (currentBlockType != "Paragraph") flushBlock();
                    currentBlockType = "Paragraph";
                    currentBlockLines.Add(line);
                }
            }
        }

        flushBlock();
    }

    private static Block CreateHeader(int level, string text)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 4), FontWeight = FontWeights.Bold };
        if (level == 1) { p.FontSize = 18; p.Foreground = s_fgH1; }
        else if (level == 2) { p.FontSize = 15; p.Foreground = s_fgH2; }
        else { p.FontSize = 13; p.Foreground = s_fgH3; }

        ParseInline(p, text);
        return p;
    }

    private static Block CreateParagraph(string text)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
        ParseInline(p, text);
        return p;
    }

    private static Block CreateList(List<string> lines, bool isOrdered)
    {
        var list = new List { MarkerStyle = isOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, MarkerOffset = 10, Margin = new Thickness(0, 0, 0, 6) };
        
        ListItem? currentItem = null;
        string currentItemText = "";

        Action flushItem = () =>
        {
            if (currentItem != null)
            {
                var p = new Paragraph();
                ParseInline(p, currentItemText.Trim());
                currentItem.Blocks.Add(p);
                list.ListItems.Add(currentItem);
            }
        };

        foreach (var line in lines)
        {
            string tLine = line.Trim();
            bool isNewItem = (isOrdered && Regex.IsMatch(tLine, @"^\d+\.\s+")) || (!isOrdered && (tLine.StartsWith("- ") || tLine.StartsWith("* ")));

            if (isNewItem)
            {
                flushItem();
                currentItem = new ListItem();
                currentItemText = Regex.Replace(tLine, isOrdered ? @"^\d+\.\s+" : @"^[-*]\s+", "");
            }
            else
            {
                currentItemText += "\n" + tLine;
            }
        }
        flushItem();

        return list;
    }

    private static void ParseInline(Paragraph p, string text)
    {
        string pattern = @"(\*\*\*.*?\*\*\*|\*\*.*?\*\*|\*.*?\*|`.*?`)";
        var parts = Regex.Split(text, pattern);

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            if (part.StartsWith("***") && part.EndsWith("***") && part.Length > 5)
            {
                p.Inlines.Add(new Run(part.Substring(3, part.Length - 6)) { FontWeight = FontWeights.Bold, FontStyle = FontStyles.Italic });
            }
            else if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 3)
            {
                p.Inlines.Add(new Run(part.Substring(2, part.Length - 4)) { FontWeight = FontWeights.Bold });
            }
            else if (part.StartsWith("*") && part.EndsWith("*") && part.Length > 1)
            {
                p.Inlines.Add(new Run(part.Substring(1, part.Length - 2)) { FontStyle = FontStyles.Italic });
            }
            else if (part.StartsWith("`") && part.EndsWith("`") && part.Length > 1)
            {
                var runContent = part.Substring(1, part.Length - 2);
                p.Inlines.Add(new Run($" {runContent} ") 
                { 
                    Background = s_bgCodeBlock, 
                    Foreground = s_fgCode, 
                    FontFamily = s_consolas 
                });
            }
            else
            {
                p.Inlines.Add(new Run(part) { Foreground = s_fgNormal });
            }
        }
    }
}
