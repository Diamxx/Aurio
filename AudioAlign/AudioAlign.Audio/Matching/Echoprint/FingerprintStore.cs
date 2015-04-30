﻿using AudioAlign.Audio.Project;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace AudioAlign.Audio.Matching.Echoprint {
    public class FingerprintStore {

        private Profile profile;
        private Dictionary<AudioTrack, TrackStore> store;
        private IFingerprintCollisionMap collisionMap;

        private double[] thresholdAccept;
        private double[] thresholdReject;

        public event EventHandler<ValueEventArgs<double>> MatchingProgress;
        public event EventHandler<ValueEventArgs<List<Match>>> MatchingFinished;

        public FingerprintStore(Profile profile) {
            this.profile = profile;
            store = new Dictionary<AudioTrack, TrackStore>();
            collisionMap = new DictionaryCollisionMap();

            // Precompute the threshold function
            thresholdAccept = new double[profile.MatchingMaxFrames];
            thresholdReject = new double[profile.MatchingMaxFrames];
            double framesPerSec = (double)profile.SamplingRate / 8 / 4 / 8;
            for (int i = 0; i < thresholdAccept.Length; i++) {
                thresholdAccept[i] = profile.ThresholdAccept.Calculate(i / framesPerSec);
                thresholdReject[i] = profile.ThresholdReject.Calculate(i / framesPerSec);
            }
        }

        public IFingerprintCollisionMap CollisionMap {
            get { return collisionMap; }
        }

        public void Add(FingerprintCodeEventArgs e) {
            if (e.Codes.Count == 0) {
                return;
            }

            lock (this) {
                // Make sure there's an index list for the track
                if (!store.ContainsKey(e.AudioTrack)) {
                    store.Add(e.AudioTrack, new TrackStore());
                }

                int codeListIndex = 0;
                int codeListIndexCount = 0;
                uint frame;
                FPCode code;
                while (e.Codes.Count > codeListIndex + codeListIndexCount) {
                    int indexIndex = store[e.AudioTrack].hashes.Count;
                    frame = e.Codes[codeListIndex].Frame;
                    codeListIndexCount = 0;
                    while (e.Codes.Count > codeListIndex + codeListIndexCount && (code = e.Codes[codeListIndex + codeListIndexCount]).Frame == frame) {
                        store[e.AudioTrack].hashes.Add(code.Code);

                        // insert a track/index lookup entry for the fingerprint hash
                        collisionMap.Add(code.Code, new FingerprintHashLookupEntry(e.AudioTrack, (int)frame));

                        codeListIndexCount++;
                    }

                    if (codeListIndexCount > 0) {
                        int frameIndex = (int)frame;
                        TrackStore.IndexEntry ie;
                        if (store[e.AudioTrack].index.ContainsKey(frameIndex)) {
                            ie = store[e.AudioTrack].index[frameIndex];
                            ie.length += codeListIndexCount;
                            store[e.AudioTrack].index.Remove(frameIndex);
                        } else {
                            ie = new TrackStore.IndexEntry(indexIndex, codeListIndexCount);
                        }
                        // Add the current length of the hash list as start pointer for all hashes belonging to the current index
                        store[e.AudioTrack].index.Add(frameIndex, ie);
                    }

                    codeListIndex += codeListIndexCount;
                }
            }
        }

        public List<Match> FindMatches(uint hash) {
            List<Match> matches = new List<Match>();
            List<FingerprintHashLookupEntry> entries = collisionMap.GetValues(hash);

            for (int x = 0; x < entries.Count; x++) {
                FingerprintHashLookupEntry entry1 = entries[x];
                for (int y = x; y < entries.Count; y++) {
                    FingerprintHashLookupEntry entry2 = entries[y];
                    if (entry1.AudioTrack != entry2.AudioTrack) { // don't compare tracks with themselves

                        var store1 = store[entry1.AudioTrack];
                        var store2 = store[entry2.AudioTrack];
                        List<uint> hashes1 = store1.hashes;
                        List<uint> hashes2 = store2.hashes;
                        int index1 = entry1.Index;
                        int index2 = entry2.Index;
                        TrackStore.IndexEntry indexEntry1, indexEntry2;
                        int numTried = 0; // count of hashes tried to match
                        int numMatched = 0; // count of hashes matched
                        int frameCount = 0; // count over how many actual frames hashes were matched (an index in the store is the equivalent of a frame in the generator)
                        bool matchFound = false;

                        // Iterate through sequential frames
                        TrackStore.IndexEntry indexEntryNone = new TrackStore.IndexEntry();
                        while(true) {
                            indexEntry1 = store1.index.ContainsKey(index1) ? store1.index[index1] : indexEntryNone;
                            indexEntry2 = store2.index.ContainsKey(index2) ? store2.index[index2] : indexEntryNone;

                            // Hash collision
                            // The union of the two ranges is the total number of distinct hashes
                            // The intersection of the two ranges is the total number of similar hashes
                            // NOTE The following block calculates the number of matches (the intersection)
                            //      of the two hash lists with the Zipper intersection algorithm and relies
                            //      on the hash list sorting in the fingerprint generator.
                            //      Other approaches tried which are slower:
                            //      - n*m element by element comparison (seven though the amount of elements is reasonably small)
                            //      - concatenating the two ranges (LINQ), sorting them, and linearly iterating over, counting the duplicates (sort is slow)
                            //      - using a hash set for collision detection (hash set insertion and lookup are costly)

                            int i = indexEntry1.index;
                            int i_e = indexEntry1.index + indexEntry1.length;
                            int j = indexEntry2.index;
                            int j_e = indexEntry2.index + indexEntry2.length;
                            int intersectionCount = 0;

                            // Count intersecting hashes of a frame with the Zipper algorithm
                            while (i < i_e && j < j_e) {
                                if (hashes1[i] < hashes2[j]) {
                                    i++;
                                }
                                else if (hashes2[j] < hashes1[i]) {
                                    j++;
                                }
                                else {
                                    intersectionCount++;
                                    i++; j++;
                                }
                            }

                            numMatched += intersectionCount;
                            numTried += indexEntry1.length + indexEntry2.length - intersectionCount;

                            // Determine the next indices to check for collisions
                            int nextIndex1Increment = 0;
                            if (hashes1.Count > i_e) {
                                do {
                                    nextIndex1Increment++;
                                } while (!store1.index.ContainsKey(index1 + nextIndex1Increment));
                            }
                            int nextIndex2Increment = 0;
                            if (hashes2.Count > j_e) {
                                do {
                                    nextIndex2Increment++;
                                } while (!store2.index.ContainsKey(index2 + nextIndex2Increment));
                            }
                            int nextIndexIncrement = Math.Min(nextIndex1Increment, nextIndex2Increment);

                            index1 += nextIndexIncrement;
                            index2 += nextIndexIncrement;
                            frameCount += nextIndexIncrement;

                            // Match detection
                            // This approach trades the hash matching rate with time, i.e. the rate required
                            // for a match drops with time, by using an exponentially with time decaying threshold. 
                            // The idea is that a high matching rate after a short time is equivalent to a low matching 
                            // rate after a long time. The difficulty is to to parameterize it in such a way, that a 
                            // match is detected as fast as possible, while detecting a no-match isn't delayed too far 
                            // as it takes a lot of processing time.
                            double rate = 1d / numTried * numMatched;

                            if (frameCount >= profile.MatchingMaxFrames || rate < thresholdReject[frameCount]) {
                                break; // exit condition
                            }
                            else if (frameCount > profile.MatchingMinFrames && rate > thresholdAccept[frameCount]) {
                                matchFound = true;
                                break;
                            }

                            if (nextIndexIncrement == 0) {
                                // We reached the end of a hash list
                                break; // Break the while loop
                            }
                        }

                        if (matchFound) {
                            matches.Add(new Match {
                                Similarity = 1f / numTried * numMatched,
                                Track1 = entry1.AudioTrack,
                                Track1Time = FingerprintGenerator.FingerprintHashIndexToTimeSpan(entry1.Index),
                                Track2 = entry2.AudioTrack,
                                Track2Time = FingerprintGenerator.FingerprintHashIndexToTimeSpan(entry2.Index),
                                Source = "FP-EP"
                            });
                        }
                    }
                }
            }

            return matches;
        }

        public List<Match> FindAllMatches() {
            List<Match> matches = new List<Match>();

            var collidingKeys = collisionMap.GetCollidingKeys();
            Debug.WriteLine("{0} colliding keys, {1} lookup entries", collidingKeys.Count, collidingKeys.Sum(h => collisionMap.GetValues(h).Count));
            
            int collisions = collidingKeys.Count;
            int count = 0;
            foreach (uint hash in collidingKeys) {
                matches.AddRange(FindMatches(hash));

                if (count++ % 4096 == 0 && MatchingProgress != null) {
                    MatchingProgress(this, new ValueEventArgs<double>((double)count / collisions * 100));
                }
            }
            Debug.WriteLine("{0} matches", matches.Count);

            if (MatchingFinished != null) {
                MatchingFinished(this, new ValueEventArgs<List<Match>>(matches));
            }
            
            return matches;
        }

        private class TrackStore {

            [DebuggerDisplay("{index}/{length}")]
            public struct IndexEntry {
                
                public int index;
                public int length;

                public IndexEntry(int index, int length) {
                    this.index = index;
                    this.length = length;
                }
            }

            public List<uint> hashes;
            public Dictionary<int, IndexEntry> index;

            public TrackStore() {
                hashes = new List<uint>();
                index = new Dictionary<int, IndexEntry>();
            }
        }
    }
}
