using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Baird.ViewModels
{
    /// <summary>
    /// ViewModel representing a single row of Seerrch result items (up to 6 items) for virtualizing grid layouts.
    /// Used to enable UI virtualization while maintaining a multi-column grid appearance.
    /// </summary>
    public class SeerrchRowViewModel : ReactiveObject
    {
        public SeerrchResultViewModel? Item1 { get; }
        public SeerrchResultViewModel? Item2 { get; }
        public SeerrchResultViewModel? Item3 { get; }
        public SeerrchResultViewModel? Item4 { get; }
        public SeerrchResultViewModel? Item5 { get; }
        public SeerrchResultViewModel? Item6 { get; }

        public SeerrchRowViewModel(
            SeerrchResultViewModel? item1 = null,
            SeerrchResultViewModel? item2 = null,
            SeerrchResultViewModel? item3 = null,
            SeerrchResultViewModel? item4 = null,
            SeerrchResultViewModel? item5 = null,
            SeerrchResultViewModel? item6 = null)
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
        public static SeerrchRowViewModel[] CreateRows(IEnumerable<SeerrchResultViewModel> items)
        {
            var itemsList = items.ToList();
            var rowCount = (int)Math.Ceiling(itemsList.Count / 6.0);
            var rows = new SeerrchRowViewModel[rowCount];

            for (int i = 0; i < rowCount; i++)
            {
                var startIndex = i * 6;
                rows[i] = new SeerrchRowViewModel(
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
