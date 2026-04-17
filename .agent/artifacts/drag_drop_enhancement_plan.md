# Drag & Drop Enhancement Implementation Plan

**Created**: 2026-01-20  
**Status**: Planning  
**Priority Features**: High and Medium Impact Items

---

## Overview

This plan details the implementation of 6 key enhancements to the EdgeLoop application's drag and drop functionality, ordered by priority and dependency.

---

## Phase 1: Quick Wins (Estimated: 1-2 hours)

### 1.1 Recursive Folder Drop
**Impact**: High | **Effort**: Low

#### Objective
When a user drops a folder onto the playlist, recursively scan all subfolders for video files and add them.

#### Files to Modify
- `EdgeLoop/ViewModels/LauncherViewModel.cs`

#### Implementation Steps

1. **Modify `AddDroppedFilesAsync` method** (Line ~1081):
   ```csharp
   private async Task AddDroppedFilesAsync(string[] files) {
       var allVideoFiles = new List<string>();
       
       foreach (var path in files) {
           if (Directory.Exists(path)) {
               // Recursively get all video files from folder
               try {
                   var folderVideos = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                       .Where(f => Constants.VideoExtensions.Contains(Path.GetExtension(f)?.ToLowerInvariant()))
                       .ToList();
                   allVideoFiles.AddRange(folderVideos);
                   Logger.Info($"Found {folderVideos.Count} videos in folder: {path}");
               } catch (UnauthorizedAccessException ex) {
                   Logger.Warning($"Access denied to folder: {path}", ex);
               }
           } else {
               var ext = Path.GetExtension(path)?.ToLowerInvariant();
               if (Constants.VideoExtensions.Contains(ext)) {
                   allVideoFiles.Add(path);
               }
           }
       }
       
       if (allVideoFiles.Count > 0) {
           await AddFilesAsync(allVideoFiles.ToArray(), _cancellationTokenSource.Token);
       }
   }
   ```

2. **Add status feedback for large folder scans**:
   - Show "Scanning folders..." message while enumerating
   - Show "Added X videos from Y folders" on completion

#### Testing
- Drop a folder with 5 videos in root
- Drop a folder with nested subfolders containing videos
- Drop a folder with no videos (should show appropriate message)
- Drop a mix of files and folders

---

### 1.2 URL Drop Support
**Impact**: Medium | **Effort**: Low

#### Objective
Allow users to drag URLs directly from their browser into the playlist.

#### Files to Modify
- `EdgeLoop/Windows/LauncherWindow.xaml.cs`
- `EdgeLoop/ViewModels/LauncherViewModel.cs`

#### Implementation Steps

1. **Update `AddedFilesList_DragOver`** (LauncherWindow.xaml.cs, Line ~266):
   ```csharp
   private void AddedFilesList_DragOver(object sender, DragEventArgs e) {
       if (e.Data.GetDataPresent(DataFormats.FileDrop)) {
           e.Effects = DragDropEffects.Copy;
       } else if (e.Data.GetDataPresent("VideoItem")) {
           e.Effects = DragDropEffects.Move;
       } else if (e.Data.GetDataPresent(DataFormats.Text) || 
                  e.Data.GetDataPresent(DataFormats.UnicodeText)) {
           // Check if text is a valid URL
           var text = e.Data.GetData(DataFormats.UnicodeText) as string 
                   ?? e.Data.GetData(DataFormats.Text) as string;
           if (!string.IsNullOrEmpty(text) && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) 
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) {
               e.Effects = DragDropEffects.Link;
           } else {
               e.Effects = DragDropEffects.None;
           }
       } else {
           e.Effects = DragDropEffects.None;
       }
       e.Handled = true;
   }
   ```

2. **Update `AddedFilesList_Drop`** (LauncherWindow.xaml.cs, Line ~277):
   ```csharp
   private void AddedFilesList_Drop(object sender, DragEventArgs e) {
       if (e.Data.GetDataPresent(DataFormats.FileDrop)) {
           string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
           ViewModel?.AddDroppedFiles(files);
       } else if (e.Data.GetDataPresent(DataFormats.Text) || 
                  e.Data.GetDataPresent(DataFormats.UnicodeText)) {
           var text = e.Data.GetData(DataFormats.UnicodeText) as string 
                   ?? e.Data.GetData(DataFormats.Text) as string;
           if (!string.IsNullOrEmpty(text)) {
               ViewModel?.AddDroppedUrl(text.Trim());
           }
       } else if (e.Data.GetDataPresent("VideoItem")) {
           // ... existing reorder logic ...
       }
   }
   ```

3. **Add `AddDroppedUrl` method** to LauncherViewModel:
   ```csharp
   public void AddDroppedUrl(string url) {
       if (string.IsNullOrWhiteSpace(url)) return;
       
       // Validate URL
       if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
           (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
           SetStatusMessage("Invalid URL dropped", StatusMessageType.Warning);
           return;
       }
       
       _ = ProcessUrlAsync(url, _cancellationTokenSource.Token);
   }
   ```

#### Testing
- Drag a YouTube URL from browser address bar
- Drag a video link from a webpage
- Drag plain text that's not a URL (should be rejected)
- Drag an HTML link element

---

## Phase 2: Visual Feedback (Estimated: 3-4 hours)

### 2.1 Drop Zone Highlighting
**Impact**: Medium | **Effort**: Low

#### Objective
Add a visual glow/highlight to the playlist area when a valid drop is hovering over it.

#### Files to Modify
- `EdgeLoop/Windows/LauncherWindow.xaml`
- `EdgeLoop/Windows/LauncherWindow.xaml.cs`

#### Implementation Steps

1. **Add a named Border around ListView** (LauncherWindow.xaml, around Line 335):
   ```xml
   <Border x:Name="PlaylistDropZone" 
           CornerRadius="8" 
           BorderThickness="2"
           BorderBrush="Transparent"
           Margin="0,0,0,0">
       <Border.Style>
           <Style TargetType="Border">
               <Style.Triggers>
                   <Trigger Property="Tag" Value="DragOver">
                       <Setter Property="BorderBrush" Value="{StaticResource AccentPrimary}"/>
                       <Setter Property="Effect">
                           <Setter.Value>
                               <DropShadowEffect Color="#FF69B4" BlurRadius="15" ShadowDepth="0" Opacity="0.6"/>
                           </Setter.Value>
                       </Setter>
                   </Trigger>
               </Style.Triggers>
           </Style>
       </Border.Style>
       
       <!-- Existing ListView goes here -->
       <ListView x:Name="AddedFilesList" ... />
   </Border>
   ```

2. **Add DragEnter/DragLeave handlers** (LauncherWindow.xaml.cs):
   ```csharp
   private void AddedFilesList_DragEnter(object sender, DragEventArgs e) {
       if (e.Data.GetDataPresent(DataFormats.FileDrop) ||
           e.Data.GetDataPresent(DataFormats.Text) ||
           e.Data.GetDataPresent("VideoItem")) {
           PlaylistDropZone.Tag = "DragOver";
       }
   }
   
   private void AddedFilesList_DragLeave(object sender, DragEventArgs e) {
       PlaylistDropZone.Tag = null;
   }
   
   // Also clear in Drop handler:
   private void AddedFilesList_Drop(object sender, DragEventArgs e) {
       PlaylistDropZone.Tag = null;
       // ... rest of drop logic
   }
   ```

3. **Wire up events in XAML**:
   ```xml
   <ListView ... 
             DragEnter="AddedFilesList_DragEnter"
             DragLeave="AddedFilesList_DragLeave" />
   ```

#### Testing
- Drag file over playlist → border glows
- Drag away → glow disappears
- Drop file → glow disappears
- Drag invalid content → no glow

---

### 2.2 Visual Insertion Indicator (Adorner)
**Impact**: High | **Effort**: Medium

#### Objective
Show a horizontal line between playlist items to indicate exactly where a dragged item will be inserted.

#### Files to Create
- `EdgeLoop/Classes/InsertionAdorner.cs` (New file)

#### Files to Modify
- `EdgeLoop/Windows/LauncherWindow.xaml.cs`

#### Implementation Steps

1. **Create InsertionAdorner class**:
   ```csharp
   // EdgeLoop/Classes/InsertionAdorner.cs
   using System.Windows;
   using System.Windows.Documents;
   using System.Windows.Media;
   
   namespace EdgeLoop.Classes {
       public class InsertionAdorner : Adorner {
           private readonly bool _isSeparatorHorizontal;
           private readonly AdornerLayer _adornerLayer;
           private readonly Pen _pen;
           private readonly bool _isAfter;
           
           public InsertionAdorner(UIElement adornedElement, bool isAfter, AdornerLayer adornerLayer) 
               : base(adornedElement) {
               _isAfter = isAfter;
               _adornerLayer = adornerLayer;
               _isSeparatorHorizontal = true;
               
               // Pink accent color matching app theme
               _pen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x69, 0xB4)), 3);
               _pen.Freeze();
               
               IsHitTestVisible = false;
               _adornerLayer.Add(this);
           }
           
           protected override void OnRender(DrawingContext drawingContext) {
               var adornedRect = new Rect(AdornedElement.RenderSize);
               
               if (_isSeparatorHorizontal) {
                   // Draw horizontal line at top or bottom of item
                   double y = _isAfter ? adornedRect.Bottom : adornedRect.Top;
                   var startPoint = new Point(adornedRect.Left + 10, y);
                   var endPoint = new Point(adornedRect.Right - 10, y);
                   
                   drawingContext.DrawLine(_pen, startPoint, endPoint);
                   
                   // Draw small triangles at ends for polish
                   DrawTriangle(drawingContext, startPoint, true);
                   DrawTriangle(drawingContext, endPoint, false);
               }
           }
           
           private void DrawTriangle(DrawingContext dc, Point point, bool isLeft) {
               var geometry = new StreamGeometry();
               using (var ctx = geometry.Open()) {
                   double direction = isLeft ? 1 : -1;
                   ctx.BeginFigure(point, true, true);
                   ctx.LineTo(new Point(point.X + (6 * direction), point.Y - 4), true, false);
                   ctx.LineTo(new Point(point.X + (6 * direction), point.Y + 4), true, false);
               }
               geometry.Freeze();
               dc.DrawGeometry(_pen.Brush, null, geometry);
           }
           
           public void Detach() {
               _adornerLayer.Remove(this);
           }
       }
   }
   ```

2. **Add adorner management to LauncherWindow.xaml.cs**:
   ```csharp
   private InsertionAdorner _insertionAdorner;
   private ListViewItem _lastAdornedItem;
   
   private void AddedFilesList_DragOver(object sender, DragEventArgs e) {
       // ... existing effect logic ...
       
       // Update insertion indicator for reorder operations
       if (e.Data.GetDataPresent("VideoItem")) {
           UpdateInsertionAdorner(e);
       } else {
           RemoveInsertionAdorner();
       }
       
       e.Handled = true;
   }
   
   private void UpdateInsertionAdorner(DragEventArgs e) {
       var pos = e.GetPosition(AddedFilesList);
       var result = VisualTreeHelper.HitTest(AddedFilesList, pos);
       
       if (result?.VisualHit != null) {
           var item = FindAncestor<ListViewItem>(result.VisualHit as DependencyObject);
           if (item != null && item != _lastAdornedItem) {
               RemoveInsertionAdorner();
               
               // Determine if inserting before or after based on mouse Y position
               var itemPos = e.GetPosition(item);
               bool isAfter = itemPos.Y > item.ActualHeight / 2;
               
               var layer = AdornerLayer.GetAdornerLayer(item);
               if (layer != null) {
                   _insertionAdorner = new InsertionAdorner(item, isAfter, layer);
                   _lastAdornedItem = item;
               }
           }
       }
   }
   
   private void RemoveInsertionAdorner() {
       _insertionAdorner?.Detach();
       _insertionAdorner = null;
       _lastAdornedItem = null;
   }
   
   // Make sure to call RemoveInsertionAdorner in DragLeave and Drop handlers
   ```

#### Testing
- Drag item over another item → line appears
- Move up/down → line updates position
- Release → line disappears
- Leave area → line disappears

---

### 2.3 Drag Ghost/Preview Image
**Impact**: Medium | **Effort**: Medium

#### Objective
Show a semi-transparent copy of the dragged item following the cursor.

#### Files to Create
- `EdgeLoop/Classes/DragAdorner.cs` (New file)

#### Files to Modify
- `EdgeLoop/Windows/LauncherWindow.xaml.cs`

#### Implementation Steps

1. **Create DragAdorner class**:
   ```csharp
   // EdgeLoop/Classes/DragAdorner.cs
   using System.Windows;
   using System.Windows.Documents;
   using System.Windows.Media;
   
   namespace EdgeLoop.Classes {
       public class DragAdorner : Adorner {
           private readonly UIElement _child;
           private double _leftOffset;
           private double _topOffset;
           
           public DragAdorner(UIElement adornedElement, UIElement draggedElement, double opacity = 0.7) 
               : base(adornedElement) {
               var brush = new VisualBrush(draggedElement) { Opacity = opacity };
               
               var bounds = VisualTreeHelper.GetDescendantBounds(draggedElement);
               var rectangle = new System.Windows.Shapes.Rectangle {
                   Width = bounds.Width,
                   Height = bounds.Height,
                   Fill = brush,
                   IsHitTestVisible = false
               };
               
               _child = rectangle;
               IsHitTestVisible = false;
           }
           
           public void SetPosition(double left, double top) {
               _leftOffset = left;
               _topOffset = top;
               
               var layer = Parent as AdornerLayer;
               layer?.Update(AdornedElement);
           }
           
           protected override Size MeasureOverride(Size constraint) {
               _child.Measure(constraint);
               return _child.DesiredSize;
           }
           
           protected override Size ArrangeOverride(Size finalSize) {
               _child.Arrange(new Rect(_child.DesiredSize));
               return finalSize;
           }
           
           public override GeneralTransform GetDesiredTransform(GeneralTransform transform) {
               var result = new GeneralTransformGroup();
               result.Children.Add(new TranslateTransform(_leftOffset, _topOffset));
               var baseTransform = base.GetDesiredTransform(transform);
               if (baseTransform != null) {
                   result.Children.Add(baseTransform);
               }
               return result;
           }
           
           protected override Visual GetVisualChild(int index) => _child;
           protected override int VisualChildrenCount => 1;
       }
   }
   ```

2. **Integrate into drag operations**:
   ```csharp
   private DragAdorner _dragAdorner;
   private Point _dragStartPoint;
   
   private void AddedFilesList_MouseMove(object sender, MouseEventArgs e) {
       // ... existing threshold check ...
       
       if (shouldStartDrag) {
           // Create ghost adorner
           var listView = sender as ListView;
           var listViewItem = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
           if (listViewItem != null) {
               var layer = AdornerLayer.GetAdornerLayer(listView);
               if (layer != null) {
                   _dragAdorner = new DragAdorner(listView, listViewItem, 0.6);
                   layer.Add(_dragAdorner);
               }
           }
           
           // Start drag operation
           DragDrop.DoDragDrop(listViewItem, dragData, DragDropEffects.Move);
           
           // Clean up
           if (_dragAdorner != null) {
               layer?.Remove(_dragAdorner);
               _dragAdorner = null;
           }
       }
   }
   
   // Update ghost position in DragOver or using QueryContinueDrag
   private void AddedFilesList_QueryContinueDrag(object sender, QueryContinueDragEventArgs e) {
       if (_dragAdorner != null) {
           var pos = GetMousePosition(); // P/Invoke for screen coords
           var listViewPos = AddedFilesList.PointFromScreen(pos);
           _dragAdorner.SetPosition(listViewPos.X - 20, listViewPos.Y - 20);
       }
   }
   ```

#### Testing
- Start dragging → ghost appears
- Move around → ghost follows cursor
- Drop or cancel → ghost disappears

---

### 2.4 Auto-Scroll During Drag
**Impact**: Medium | **Effort**: Low

#### Objective
Automatically scroll the playlist when dragging near the top or bottom edge.

#### Files to Modify
- `EdgeLoop/Windows/LauncherWindow.xaml.cs`

#### Implementation Steps

1. **Add auto-scroll logic to DragOver**:
   ```csharp
   private ScrollViewer _playlistScrollViewer;
   
   private void AddedFilesList_DragOver(object sender, DragEventArgs e) {
       // ... existing logic ...
       
       // Auto-scroll when near edges
       AutoScrollDuringDrag(e);
   }
   
   private void AutoScrollDuringDrag(DragEventArgs e) {
       if (_playlistScrollViewer == null) {
           _playlistScrollViewer = FindVisualChild<ScrollViewer>(AddedFilesList);
       }
       
       if (_playlistScrollViewer == null) return;
       
       var pos = e.GetPosition(AddedFilesList);
       double scrollMargin = 40; // pixels from edge to trigger scroll
       double scrollSpeed = 5;   // pixels per tick
       
       if (pos.Y < scrollMargin) {
           // Scroll up - speed increases as mouse gets closer to edge
           double intensity = 1 - (pos.Y / scrollMargin);
           _playlistScrollViewer.ScrollToVerticalOffset(
               _playlistScrollViewer.VerticalOffset - (scrollSpeed * intensity * 2));
       } else if (pos.Y > AddedFilesList.ActualHeight - scrollMargin) {
           // Scroll down
           double intensity = 1 - ((AddedFilesList.ActualHeight - pos.Y) / scrollMargin);
           _playlistScrollViewer.ScrollToVerticalOffset(
               _playlistScrollViewer.VerticalOffset + (scrollSpeed * intensity * 2));
       }
   }
   
   private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject {
       for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
           var child = VisualTreeHelper.GetChild(parent, i);
           if (child is T typedChild) return typedChild;
           var result = FindVisualChild<T>(child);
           if (result != null) return result;
       }
       return null;
   }
   ```

#### Testing
- Add 20+ videos to playlist
- Drag item to bottom edge → list scrolls down
- Drag item to top edge → list scrolls up
- Speed increases near extreme edge

---

## Phase 3: Polish (Estimated: 1-2 hours)

### 3.1 Progress Feedback for Large Folder Imports

#### Implementation
- Add a progress counter during folder enumeration
- Show "Found X videos..." status that updates
- Allow cancellation of long-running scans

### 3.2 Keyboard Accessibility

#### Implementation
- Support moving items with Alt+Up/Down arrows
- Support deleting with Delete key
- Support selecting all with Ctrl+A

---

## Summary & Dependencies

```
Phase 1 (No dependencies)
├── 1.1 Recursive Folder Drop
└── 1.2 URL Drop Support

Phase 2 (Can be done in parallel)
├── 2.1 Drop Zone Highlighting
├── 2.2 Insertion Indicator (requires InsertionAdorner.cs)
├── 2.3 Drag Ghost (requires DragAdorner.cs)
└── 2.4 Auto-Scroll

Phase 3 (After Phase 1 & 2)
├── 3.1 Progress Feedback
└── 3.2 Keyboard Accessibility
```

---

## Estimated Total Time

| Phase | Features | Estimated Time |
|-------|----------|----------------|
| Phase 1 | Folder + URL Drop | 1-2 hours |
| Phase 2 | Visual Feedback (all 4) | 3-4 hours |
| Phase 3 | Polish | 1-2 hours |
| **Total** | | **5-8 hours** |

---

## Files Changed Summary

| File | Changes |
|------|---------|
| `LauncherWindow.xaml` | Drop zone border, event wiring |
| `LauncherWindow.xaml.cs` | All drag handlers, adorner management |
| `LauncherViewModel.cs` | `AddDroppedFilesAsync`, `AddDroppedUrl` |
| `InsertionAdorner.cs` | NEW - Insertion line visual |
| `DragAdorner.cs` | NEW - Ghost preview visual |

