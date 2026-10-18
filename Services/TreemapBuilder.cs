using System.Diagnostics;
using DiagnoX.Models;

namespace DiagnoX.Services;

public sealed class TreemapBuilder
{
    private const int MaxItemsToRender = 400; // Optimal balance between detail and 60fps rendering

    public List<TreemapItem> Build(FileSystemNode rootNode, double width, double height)
    {
        var result = new List<TreemapItem>();
        if (rootNode.Size <= 0 || width <= 10 || height <= 10) return result;

        // Collect items to display: prioritize significant files and directories
        var candidateNodes = new List<FileSystemNode>();
        CollectSignificantNodes(rootNode, candidateNodes, rootNode.Size, MaxItemsToRender);

        if (candidateNodes.Count == 0) return result;

        // Sort descending by size
        candidateNodes = candidateNodes.OrderByDescending(n => n.Size).ToList();

        // Run squarified treemap layout algorithm
        var totalSize = candidateNodes.Sum(n => (double)n.Size);
        if (totalSize <= 0) return result;

        Squarify(candidateNodes, 0, 0, width, height, totalSize, result);

        return result;
    }

    private static void CollectSignificantNodes(
        FileSystemNode node,
        List<FileSystemNode> collected,
        long rootSize,
        int maxItems)
    {
        if (collected.Count >= maxItems) return;

        // If the node is a file, collect it
        if (!node.IsDirectory)
        {
            collected.Add(node);
            return;
        }

        // If it's a directory with no children, nothing to collect
        if (node.Children.Count == 0)
        {
            if (node.Size > 0) collected.Add(node);
            return;
        }

        // If it's a directory, examine children
        foreach (var child in node.Children)
        {
            if (collected.Count >= maxItems) break;

            // Prune insignificant noise (< 0.02% of root size)
            if (rootSize > 0 && (double)child.Size / rootSize < 0.0002)
            {
                continue;
            }

            if (!child.IsDirectory)
            {
                collected.Add(child);
            }
            else
            {
                // If directory has children, recurse into significant children
                if (child.Children.Count > 0 && child.Children.Any(c => !c.IsDirectory || c.Children.Count > 0))
                {
                    CollectSignificantNodes(child, collected, rootSize, maxItems);
                }
                else
                {
                    collected.Add(child);
                }
            }
        }
    }

    private static void Squarify(
        List<FileSystemNode> items,
        double x,
        double y,
        double w,
        double h,
        double totalWeight,
        List<TreemapItem> result)
    {
        if (items.Count == 0 || w <= 0 || h <= 0 || totalWeight <= 0) return;

        var remaining = new List<FileSystemNode>(items);
        var curX = x;
        var curY = y;
        var curW = w;
        var curH = h;
        var curTotal = totalWeight;

        while (remaining.Count > 0 && curW > 0.5 && curH > 0.5)
        {
            var row = new List<FileSystemNode>();
            var isHoriz = curW >= curH;
            var length = isHoriz ? curH : curW;

            row.Add(remaining[0]);
            remaining.RemoveAt(0);

            var rowWeight = (double)row[0].Size;

            while (remaining.Count > 0)
            {
                var next = remaining[0];
                var nextWeight = (double)next.Size;
                var currentWorst = WorstAspectRatio(row, rowWeight, length, curTotal, isHoriz ? curW : curH);
                
                row.Add(next);
                var testWeight = rowWeight + nextWeight;
                var testWorst = WorstAspectRatio(row, testWeight, length, curTotal, isHoriz ? curW : curH);

                if (testWorst <= currentWorst)
                {
                    remaining.RemoveAt(0);
                    rowWeight = testWeight;
                }
                else
                {
                    row.RemoveAt(row.Count - 1);
                    break;
                }
            }

            // Layout row
            var rowFraction = curTotal > 0 ? rowWeight / curTotal : 0;
            var rowWidth = isHoriz ? curW * rowFraction : curW;
            var rowHeight = isHoriz ? curH : curH * rowFraction;

            var itemX = curX;
            var itemY = curY;

            foreach (var item in row)
            {
                var itemFraction = rowWeight > 0 ? (double)item.Size / rowWeight : 0;
                var iw = isHoriz ? rowWidth : curW * itemFraction;
                var ih = isHoriz ? curH * itemFraction : rowHeight;

                if (iw >= 1 && ih >= 1)
                {
                    result.Add(new TreemapItem
                    {
                        X = itemX,
                        Y = itemY,
                        Width = Math.Max(0.5, iw),
                        Height = Math.Max(0.5, ih),
                        Node = item,
                        ColorHex = item.ColorHex
                    });
                }

                if (isHoriz) itemY += ih;
                else itemX += iw;
            }

            // Advance remaining rectangle
            if (isHoriz)
            {
                curX += rowWidth;
                curW = Math.Max(0, curW - rowWidth);
            }
            else
            {
                curY += rowHeight;
                curH = Math.Max(0, curH - rowHeight);
            }

            curTotal -= rowWeight;
        }
    }

    private static double WorstAspectRatio(
        List<FileSystemNode> row,
        double rowWeight,
        double sideLength,
        double totalWeight,
        double availableLength)
    {
        if (sideLength <= 0 || rowWeight <= 0 || totalWeight <= 0) return double.MaxValue;

        var rowThickness = (rowWeight / totalWeight) * availableLength;
        if (rowThickness <= 0) return double.MaxValue;

        var worst = 1.0;
        foreach (var item in row)
        {
            var itemFraction = (double)item.Size / rowWeight;
            var itemLength = sideLength * itemFraction;
            if (itemLength <= 0) continue;

            var ratio = Math.Max(rowThickness / itemLength, itemLength / rowThickness);
            if (ratio > worst) worst = ratio;
        }

        return worst;
    }
}
