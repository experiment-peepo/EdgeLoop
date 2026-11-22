using System;
using System.Collections.Generic;
using System.Linq;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// The order Shuffle plays a playlist in: rounds that each play every video once, in a
    /// random order. A round never starts with the video that ended the one before it.
    /// </summary>
    /// <remarks>
    /// Keeps the previous round so Previous goes back to the videos actually watched, and draws
    /// the next round early when asked what's coming, so the videos that start it can be
    /// buffered ahead like any other.
    /// </remarks>
    public sealed class ShuffleOrder
    {
        private readonly Random _random;
        private int[] _round;     // This round's order
        private int[] _previous;  // The round before, so Back can step into it
        private int[] _next;      // The round after, once drawn early or stepped back from
        private int _pointer = -1; // Position in _round of the video playing now

        public ShuffleOrder(Random random = null)
        {
            _random = random ?? Random.Shared;
        }

        /// <summary>Position in this round (0-based) of the video playing now, or -1.</summary>
        public int Position => _pointer;

        /// <summary>Number of videos in this round, or 0 before one is drawn.</summary>
        public int RoundLength => _round?.Length ?? 0;

        /// <summary>Forgets the order, for when the playlist changes.</summary>
        public void Reset()
        {
            _round = _previous = _next = null;
            _pointer = -1;
        }

        /// <summary>Starts a new order for <paramref name="count"/> videos that plays <paramref name="firstIndex"/> now.</summary>
        public void StartAt(int count, int firstIndex)
        {
            _previous = _next = null;
            _round = Draw(count);
            int idx = Array.IndexOf(_round, firstIndex);
            if (idx > 0) (_round[0], _round[idx]) = (_round[idx], _round[0]);
            _pointer = 0;
        }

        /// <summary>
        /// Moves on to the next video and returns it. Starts a new round when this one is done,
        /// or when there is no order yet for this many videos (not starting with <paramref name="current"/>).
        /// </summary>
        public int Next(int count, int current)
        {
            if (count <= 0) return -1;

            if (!Fits(_round, count))
            {
                _previous = _next = null;
                _round = Draw(count, avoidFirst: current);
                _pointer = -1;
            }
            else if (_pointer >= _round.Length - 1)
            {
                var next = Fits(_next, count) ? _next : Draw(count, avoidFirst: _round[^1]);
                _previous = _round;
                _round = next;
                _next = null;
                _pointer = -1;
            }

            _pointer++;
            return _round[_pointer];
        }

        /// <summary>
        /// Steps back to the video that played before this one, into the previous round if needed,
        /// and returns it. Returns -1 when nothing earlier is known.
        /// </summary>
        public int Back(int count)
        {
            if (!Fits(_round, count)) return -1;

            if (_pointer > 0)
            {
                _pointer--;
            }
            else if (Fits(_previous, count))
            {
                // Keep this round, so going forward again replays it in the same order
                _next = _round;
                _round = _previous;
                _previous = null;
                _pointer = _round.Length - 1;
            }
            else
            {
                return -1;
            }

            return _round[_pointer];
        }

        /// <summary>
        /// Follows a jump to <paramref name="index"/> (a video picked by hand): carries on from
        /// its place in this round, or starts a new order with it. Does nothing before an order exists.
        /// </summary>
        public void MoveTo(int count, int index)
        {
            if (_round == null) return;
            int idx = Fits(_round, count) ? Array.IndexOf(_round, index) : -1;
            if (idx >= 0) _pointer = idx;
            else StartAt(count, index);
        }

        /// <summary>
        /// The next <paramref name="howMany"/> videos after the one playing now, in the order they'll
        /// play. Draws the next round if this one ends sooner. Empty before an order exists.
        /// </summary>
        public List<int> Upcoming(int count, int howMany)
        {
            var upcoming = new List<int>();
            if (!Fits(_round, count) || howMany <= 0) return upcoming;

            for (int p = _pointer + 1; p < _round.Length && upcoming.Count < howMany; p++)
                upcoming.Add(_round[p]);

            if (upcoming.Count < howMany)
            {
                if (!Fits(_next, count)) _next = Draw(count, avoidFirst: _round[^1]);
                for (int p = 0; p < _next.Length && upcoming.Count < howMany; p++)
                    upcoming.Add(_next[p]);
            }

            return upcoming;
        }

        private static bool Fits(int[] round, int count) => round != null && round.Length == count;

        private int[] Draw(int count, int avoidFirst = -1)
        {
            var order = Enumerable.Range(0, count).ToArray();

            // Fisher-Yates shuffle
            for (int i = order.Length - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }

            if (avoidFirst >= 0 && order.Length > 1 && order[0] == avoidFirst)
            {
                int swapIdx = _random.Next(1, order.Length);
                (order[0], order[swapIdx]) = (order[swapIdx], order[0]);
            }
            return order;
        }
    }
}
