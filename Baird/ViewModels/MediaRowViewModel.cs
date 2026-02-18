using ReactiveUI;

namespace Baird.ViewModels
{
    /// <summary>
    /// ViewModel representing a single row of media items (up to 6 items) for virtualizing grid layouts.
    /// Used to enable UI virtualization while maintaining a multi-column grid appearance.
    /// </summary>
    public class MediaRowViewModel : ReactiveObject
    {
        public MediaItemViewModel? Item1 { get; }
        public MediaItemViewModel? Item2 { get; }
        public MediaItemViewModel? Item3 { get; }
        public MediaItemViewModel? Item4 { get; }
        public MediaItemViewModel? Item5 { get; }
        public MediaItemViewModel? Item6 { get; }

        public MediaRowViewModel(
            MediaItemViewModel? item1 = null,
            MediaItemViewModel? item2 = null,
            MediaItemViewModel? item3 = null,
            MediaItemViewModel? item4 = null,
            MediaItemViewModel? item5 = null,
            MediaItemViewModel? item6 = null)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
            Item5 = item5;
            Item6 = item6;
        }

        /// <summary>
        /// Create rows from a flat list of items, grouping them into rows of up to 6 items.
        /// </summary>
        public static MediaRowViewModel[] CreateRows(IEnumerable<MediaItemViewModel> items)
        {
            var itemsList = items.ToList();
            var rowCount = (int)Math.Ceiling(itemsList.Count / 6.0);
            var rows = new MediaRowViewModel[rowCount];

            for (int i = 0; i < rowCount; i++)
            {
                var startIndex = i * 6;
                rows[i] = new MediaRowViewModel(
                    item1: startIndex < itemsList.Count ? itemsList[startIndex] : null,
                    item2: startIndex + 1 < itemsList.Count ? itemsList[startIndex + 1] : null,
                    item3: startIndex + 2 < itemsList.Count ? itemsList[startIndex + 2] : null,
                    item4: startIndex + 3 < itemsList.Count ? itemsList[startIndex + 3] : null,
                    item5: startIndex + 4 < itemsList.Count ? itemsList[startIndex + 4] : null,
                    item6: startIndex + 5 < itemsList.Count ? itemsList[startIndex + 5] : null
                );
            }

            return rows;
        }
    }
}
