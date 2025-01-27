﻿using System.Diagnostics.CodeAnalysis;

namespace DotNext.Collections.Generic
{
    [ExcludeFromCodeCoverage]
    public sealed class CollectionTests : Test
    {
        [Fact]
        public static void AddingItems()
        {
            var list = new List<int>();
            list.AddAll(new[] { 1, 3, 5 });
            Equal(1, list[0]);
            Equal(3, list[1]);
            Equal(5, list[2]);
        }

        [Fact]
        public static void LinkedListToArray()
        {
            var list = new LinkedList<int>();
            list.AddLast(10);
            list.AddLast(20);
            list.AddLast(30);

            ICollection<int> collection = list;
            Equal(new[] { 10, 20, 30 }, Generic.Collection.ToArray(collection));

            IReadOnlyCollection<int> collection2 = list;
            Equal(new[] { 10, 20, 30 }, Generic.Collection.ToArray(collection2));
        }

        [Fact]
        public static void ReadOnlyView()
        {
            var view = new ReadOnlyCollectionView<string, int>(new[] { "1", "2", "3" }, new Converter<string, int>(int.Parse));
            Equal(3, view.Count);
            NotEmpty(view);
            foreach (var value in view)
                if (!value.IsBetween(0, 3, BoundType.Closed))
                    throw new Exception();
        }

        [Fact]
        public static void PeekRandomFromEmptyCollection()
        {
            False(Array.Empty<int>().PeekRandom(new()).HasValue);
        }

        [Fact]
        public static void PeekRandomFromSingletonCollection()
        {
            Equal(5, new int[] { 5 }.PeekRandom(new()));
        }

        [Fact]
        public static void PeekRandomFromCollection()
        {
            IReadOnlyCollection<int> collection = new int[] { 10, 20, 30 };
            for (var i = 0; i < 3; i++)
            {
                var item = collection.PeekRandom(Random.Shared);
                True(item == 10 || item == 20 || item == 30);
            }
        }
    }
}
