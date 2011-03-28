﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AudioAlign.Audio.Project;
using System.Diagnostics;

namespace AudioAlign.Audio.Matching.HaitsmaKalker2002 {
    public class FingerprintStore {

        private const int FINGERPRINT_SIZE = 256;

        private Dictionary<SubFingerprint, List<SubFingerprintLookupEntry>> lookupTable;
        private Dictionary<AudioTrack, List<SubFingerprint>> store;

        public FingerprintStore() {
            lookupTable = new Dictionary<SubFingerprint, List<SubFingerprintLookupEntry>>();
            store = new Dictionary<AudioTrack, List<SubFingerprint>>();
        }

        public Dictionary<SubFingerprint, List<SubFingerprintLookupEntry>> LookupTable {
            get { return lookupTable; }
        }

        public Dictionary<AudioTrack, List<SubFingerprint>> AudioTracks {
            get { return store; }
        }

        public void Add(AudioTrack audioTrack, SubFingerprint subFingerprint, TimeSpan timestamp) {
            lock (this) {
                // store the sub-fingerprint in the sequential list of the audio track
                if (!store.ContainsKey(audioTrack)) {
                    store.Add(audioTrack, new List<SubFingerprint>());
                }
                store[audioTrack].Add(subFingerprint);

                // insert a track/index lookup entry for the sub-fingerprint
                if (!lookupTable.ContainsKey(subFingerprint)) {
                    lookupTable.Add(subFingerprint, new List<SubFingerprintLookupEntry>());
                }
                lookupTable[subFingerprint].Add(new SubFingerprintLookupEntry(audioTrack, store[audioTrack].Count, timestamp));
            }
        }

        public void Analyze() {
            Debug.WriteLine("analyzing fingerprint store...");
            foreach (SubFingerprint sfp in lookupTable.Keys) {
                if (lookupTable[sfp].Count > 1) {
                    Debug.WriteLine(sfp + " has " + lookupTable[sfp].Count + " matches:");
                    foreach (SubFingerprintLookupEntry le in lookupTable[sfp]) {
                        Debug.WriteLine(le.AudioTrack + ": " + le.Index);
                    }
                }
            }
            Debug.WriteLine("analysis finished");
        }

        public List<Match> FindMatches(SubFingerprint subFingerprint) {
            List<Match> matches = new List<Match>();
            float threshold = 0.35f;

            if (!lookupTable.ContainsKey(subFingerprint)) {
                return matches;
            }
            List<SubFingerprintLookupEntry> entries = lookupTable[subFingerprint];

            //Debug.WriteLine("Finding matches...");

            // compare each track with each other
            int cycle = 1;
            for(int x = 0; x < entries.Count; x++) {
                SubFingerprintLookupEntry entry1 = entries[x];
                for (int y = cycle; y < entries.Count; y++) {
                    SubFingerprintLookupEntry entry2 = entries[y];
                    if (entry1.AudioTrack != entry2.AudioTrack) { // don't compare tracks with themselves
                        //Debug.WriteLine("Comparing " + entry1.AudioTrack.Name + " with " + entry2.AudioTrack.Name + ":");
                        int bitErrors = 0;

                        int indexOffset = 0;
                        if (store[entry1.AudioTrack].Count - entry1.Index < FINGERPRINT_SIZE) {
                            indexOffset = Math.Min(indexOffset, -FINGERPRINT_SIZE + store[entry1.AudioTrack].Count - entry1.Index);
                        }
                        if (store[entry2.AudioTrack].Count - entry2.Index < FINGERPRINT_SIZE) {
                            indexOffset = Math.Min(indexOffset, -FINGERPRINT_SIZE + store[entry2.AudioTrack].Count - entry2.Index);
                        }

                        if (indexOffset < 0) {
                            continue;
                        }

                        // sum up the bit errors
                        for (int s = 0; s < FINGERPRINT_SIZE; s++) {
                            bitErrors += store[entry1.AudioTrack][entry1.Index + indexOffset + s].HammingDistance(store[entry2.AudioTrack][entry2.Index + indexOffset + s]);
                        }

                        float bitErrorRate = bitErrors / 8192f; // 8192 = 256 sub-fingerprints * 32 bits
                        //Debug.WriteLine("BER: " + bitErrorRate + " <- " + (bitErrorRate < threshold ? "MATCH!!!" : "no match"));
                        if (bitErrorRate < threshold) {
                            matches.Add(new Match { 
                                Similarity = 1 - bitErrorRate, 
                                Track1 = entry1.AudioTrack, 
                                Track1Time = entry1.Timestamp, 
                                Track2 = entry2.AudioTrack, 
                                Track2Time = entry2.Timestamp 
                            });
                        }
                    }
                }
                cycle++;
            }

            //Debug.WriteLine("finished");
            return matches;
        }

        public List<Match> FindSoftMatches(SubFingerprint subFingerprint) {
            List<Match> matches = new List<Match>();
            matches.AddRange(FindMatches(subFingerprint));
            if (matches.Count == 0) {
                // no match found, generate 32 subfingerprints of distance 1
                for (int x = 0; x < 32; x++) {
                    SubFingerprint temp = new SubFingerprint(subFingerprint.Value);
                    temp[x] = !temp[x];
                    matches.AddRange(FindMatches(temp));
                }
                //if (matches.Count == 0) {
                //    // again no match found, generate 32*32 subfingerprints of distance 2
                //    for (int i = 0; i < 32; i++) {
                //        for (int j = i + 1; j < 32; j++) {
                //            SubFingerprint temp = new SubFingerprint(subFingerprint.Value);
                //            temp[i] = !temp[i];
                //            temp[j] = !temp[j];
                //            matches.AddRange(FindMatches(temp));
                //        }
                //    }
                //}
            }
            return matches;
        }

        public List<Match> FindAllMatchingMatches() {
            List<Match> matches = new List<Match>();
            foreach (SubFingerprint subFingerprint in lookupTable.Keys) {
                matches.AddRange(FindMatches(subFingerprint));
            }
            return matches;
        }

        public List<Match> FindAllMatches() {
            List<Match> matches = new List<Match>();
            foreach (AudioTrack audioTrack in store.Keys) {
                foreach (SubFingerprint subFingerprint in store[audioTrack]) {
                    matches.AddRange(FindSoftMatches(subFingerprint));
                }
            }
            return matches;
        }

        public Fingerprint GetFingerprint(SubFingerprintLookupEntry entry) {
            int indexOffset = 0;
            if (store[entry.AudioTrack].Count - entry.Index < FINGERPRINT_SIZE) {
                indexOffset = Math.Min(indexOffset, -FINGERPRINT_SIZE + store[entry.AudioTrack].Count - entry.Index);
            }
            return new Fingerprint(store[entry.AudioTrack], entry.Index + indexOffset, FINGERPRINT_SIZE);
        }

        public void PrintStats() {
            int totalSize = 0;
            Debug.WriteLine("calculating fingerprintstore stats:");
            
            // calculate subfingerprint size for each track
            foreach (AudioTrack audioTrack in store.Keys) {
                int subFingerprintCount = store[audioTrack].Count;
                Debug.WriteLine(audioTrack + ": " + (subFingerprintCount * sizeof(UInt32)) + " bytes");
                totalSize += subFingerprintCount * sizeof(UInt32);
            }

            // calculate lookuptable size
            int lookupCount = 0;
            foreach (SubFingerprint subFingerpint in lookupTable.Keys) {
                lookupCount += lookupTable[subFingerpint].Count;
            }
            Debug.WriteLine("lookup table size: " + (lookupCount * (sizeof(int) + sizeof(int))) + " bytes");
            totalSize += lookupCount * (sizeof(int) + sizeof(int));

            Debug.WriteLine("total size: " + totalSize + " bytes = " + (totalSize / 1024) + " kb = " + (totalSize / 1024 / 1024) + " mb");
        }

        public float CalculateBER(Fingerprint fp1, Fingerprint fp2) {
            int bitErrors = 0;

            // sum up the bit errors
            for (int s = 0; s < FINGERPRINT_SIZE; s++) {
                bitErrors += fp1[s].HammingDistance(fp2[s]);
            }

            return bitErrors / 8192f; // 8192 = 256 sub-fingerprints * 32 bits
        }

        public void FindAllMatches(int maxSubFingerprintDistance, bool calculateFingerprintBER) {
            for (int i = 0; i < store.Keys.Count; i++) {
                AudioTrack audioTrack1 = store.Keys.ElementAt(i);
                for (int j = i + 1; j < store.Keys.Count; j++) {
                    AudioTrack audioTrack2 = store.Keys.ElementAt(j);
                    int sfp1Index = 0;
                    foreach (SubFingerprint subFingerprint1 in store[audioTrack1]) {
                        int sfp2Index = 0;
                        foreach (SubFingerprint subFingerprint2 in store[audioTrack2]) {
                            int sfpDistance = subFingerprint1.HammingDistance(subFingerprint2);
                            if (sfpDistance <= maxSubFingerprintDistance) {
                                float ber = 0;
                                if (calculateFingerprintBER) {
                                    ber = CalculateBER(
                                        GetFingerprint(new SubFingerprintLookupEntry(audioTrack1, sfp1Index)), 
                                        GetFingerprint(new SubFingerprintLookupEntry(audioTrack2, sfp2Index)));
                                }
                                if (ber < 0.35f) {
                                    Match match = new Match {
                                        Similarity = 1 - ber,
                                        Track1 = audioTrack1,
                                        Track1Time = FingerprintGenerator.SubFingerprintIndexToTimeSpan(sfp1Index),
                                        Track2 = audioTrack2,
                                        Track2Time = FingerprintGenerator.SubFingerprintIndexToTimeSpan(sfp2Index)
                                    };
                                    Debug.WriteLine(match + " [SFP distance: " + sfpDistance + "]");
                                }
                            }
                            sfp2Index++;
                        }
                        sfp1Index++;
                    }
                }
            }
        }
    }
}