﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Isas.SequencingFiles;
using CanvasCommon;
using CanvasPartition.Models;
using Illumina.Common.FileSystem;
using Isas.Framework.Logging;

namespace CanvasPartition
{
    internal class VafContainingBins
    {
        public int Index { get; }
        public double Vaf { get; }

        public VafContainingBins(int index, double vaf)
        {
            Index = index;
            Vaf = vaf;
        }
    }

    class SegmentationInput
    {
        public string EvennessMetricFile { get; }


        #region Members
        private string InputBinPath;
        private string InputVafPath;
        public Dictionary<string, List<VafContainingBins>> VafByChr = new Dictionary<string, List<VafContainingBins>>();
        public string ForbiddenIntervalBedPath = null;
        public CoverageInfo CoverageInfo = new CoverageInfo();
        private readonly ILogger _logger;
        private readonly ISegmentationResultsProcessor _processor;

        #endregion

        public class Segment
        {
            /// <summary>
            /// Genomic start location (zero-based inclusive)
            /// </summary>
            public uint start;

            /// <summary>
            /// Genomic end location (zero-based exclusive or one-based inclusive)
            /// </summary>
            public uint end;
        }

        public enum SegmentationMethod
        {
            Wavelets,
            CBS,
            HMM,
            PerSampleHMM
        }

        public SegmentationInput(string inputBinPath, string inputVafPath, string forbiddenBedPath, 
            string referenceFolder, string evennessMetricFile, ILogger logger, ISegmentationResultsProcessor processor, string dataType = "logratio")
        {
            EvennessMetricFile = evennessMetricFile;
            _logger = logger;
            _processor = processor;
            InputBinPath = inputBinPath;
            InputVafPath = inputVafPath;
            ForbiddenIntervalBedPath = forbiddenBedPath;
            ReadInputFiles(referenceFolder);
        }

        private void ReadInputFiles(string referenceFolder)
        {
            CoverageInfo = CanvasSegment.ReadBedInput(InputBinPath, ForbiddenIntervalBedPath);
            if (InputVafPath != null)
                LoadVAFInput(referenceFolder);
        }

        public static SegmentationInput.Segment[] DeriveSegments(List<int> breakpoints, int segmentsLength, uint[] startByChr, uint[] endByChr)
        {
            var startBreakpointsPos = new List<int>();
            var endBreakpointPos = new List<int>();
            var lengthSeg = new List<int>();
            if (breakpoints.Count >= 2 && segmentsLength > 10)
            {
                if (breakpoints[0] != 0)
                    breakpoints.Insert(0, 0);
                startBreakpointsPos.Add(breakpoints[0]);
                endBreakpointPos.Add(breakpoints[1] - 1);
                lengthSeg.Add(breakpoints[1] - 1);
                for (int i = 1; i < breakpoints.Count - 1; i++)
                {
                    startBreakpointsPos.Add(breakpoints[i]);
                    endBreakpointPos.Add(breakpoints[i + 1] - 1);
                    lengthSeg.Add(breakpoints[i + 1] - 1 - breakpoints[i]);
                }
                startBreakpointsPos.Add(breakpoints[breakpoints.Count - 1]);
                endBreakpointPos.Add(segmentsLength - 1);
                lengthSeg.Add(segmentsLength - breakpoints[breakpoints.Count - 1] - 1);
            }
            else
            {
                startBreakpointsPos.Add(0);
                endBreakpointPos.Add(segmentsLength - 1);
                lengthSeg.Add(segmentsLength - 1);
            }


            var segments = new SegmentationInput.Segment[startBreakpointsPos.Count];
            for (int i = 0; i < startBreakpointsPos.Count; i++)
            {
                int start = startBreakpointsPos[i];
                int end = endBreakpointPos[i];
                segments[i] = new SegmentationInput.Segment
                {
                    start = startByChr[start],
                    end = endByChr[end]
                };
            }
            return segments;
        }

        /// <summary>
        /// Parse the outputs of CanvasSNV, and note these variant frequencies in the appropriate segment.
        /// </summary>
        public void LoadVAFInput(string referenceFolder)
        {
            try
            {
                var vafByChr = new Dictionary<string, List<List<double>>>();
                var intervalsByChromosome = new Dictionary<string, List<BedInterval>>();

                foreach (string chr in CoverageInfo.StartByChr.Keys)
                {
                    vafByChr[chr] = new List<List<double>>(CoverageInfo.StartByChr[chr].Length);
                    intervalsByChromosome[chr] = new List<BedInterval>();
                    for (int index = 0; index < CoverageInfo.StartByChr[chr].Length; index++)
                    {
                        vafByChr[chr].Add(new List<double>());
                        intervalsByChromosome[chr].Add(new BedInterval(Convert.ToInt32(CoverageInfo.StartByChr[chr][index]),
                            Convert.ToInt32(CoverageInfo.EndByChr[chr][index])));
                    }
                }

                var alleleCountsByChromosome = CanvasIO.ReadFrequenciesWrapper(_logger, new FileLocation(this.InputVafPath), intervalsByChromosome);
                foreach (var chr in alleleCountsByChromosome.Keys)
                {
                    for (int index = 0; index < alleleCountsByChromosome[chr].Count; index++)
                    {
                        vafByChr[chr][index] = alleleCountsByChromosome[chr][index].MaxFrequencies;
                    }
                }

                foreach (string chr in vafByChr.Keys)
                {
                    VafByChr[chr] = new List<VafContainingBins>();
                    var index = 0;
                    foreach (var bin in vafByChr[chr])
                    {
                        if (bin.Count > 0)
                            VafByChr[chr].Add(new VafContainingBins(index, bin.Average()));
                        index++;
                    }
                }
                _logger.Info("Done processing VAFs\n");

            }
            catch (Exception e)
            {
                Console.Error.WriteLine("File {0} could not be read:", this.InputVafPath);
                Console.Error.WriteLine(e.Message);
                Environment.Exit(1);
            }
        }



        /// <summary>
        /// Merge segmentation breakpoints with common CNV intervals
        /// </summary>
        public static List<int> OverlapCommonRegions(List<int> breakpoints, List<BedInterval> commonCNVintervals)
        {
            var newBreakpoints = new List<int>();
            int index = 0;
            int length = commonCNVintervals.Count;
            foreach (int breakpoint in breakpoints)
            {
                while (index < length)
                {

                    if (breakpoint <= commonCNVintervals[index].Start)
                    {
                        newBreakpoints.Add(breakpoint);
                        break;
                    }
                    if (breakpoint > commonCNVintervals[index].Start && breakpoint < commonCNVintervals[index].End)
                    {
                        newBreakpoints.Add(commonCNVintervals[index].Start);
                        newBreakpoints.Add(commonCNVintervals[index].End);
                        index++;
                        break;
                    }
                    if (breakpoint >= commonCNVintervals[index].End)
                    {
                        newBreakpoints.Add(commonCNVintervals[index].Start);
                        newBreakpoints.Add(commonCNVintervals[index].End);
                        index++;
                    }
                }
                if (index > length)
                    newBreakpoints.Add(breakpoint);
            }
            return newBreakpoints;
        }


        public Dictionary<string, List<SegmentWithBins>> PostProcessSegments(
            GenomeSegmentationResults segmentationResults,
            PloidyInfo referencePloidy)
        {
            var excludedIntervals = new Dictionary<string, List<SampleGenomicBin>>();
            if (!string.IsNullOrEmpty(ForbiddenIntervalBedPath))
            {
                excludedIntervals = CanvasCommon.Utilities.LoadBedFile(ForbiddenIntervalBedPath);
            }

            return _processor.PostProcessSegments(segmentationResults, referencePloidy, excludedIntervals,
                CoverageInfo);
        }

        public void WriteCanvasPartitionResults(string outPath, Dictionary<string, List<SegmentWithBins>> segmentsByChromosome)
        {
            using (var writer = new GzipWriter(outPath))
            {
                foreach (var chr in segmentsByChromosome.Keys)
                {
                    var segments = segmentsByChromosome[chr];

                    foreach (var segment in segments)
                    {
                        foreach (var bin in segment.Bins)
                        {
                            writer.WriteLine(string.Format($"{chr}\t{bin.Start}\t{bin.End}\t{bin.Coverage}\t{segment.Identifier}"));
                        }
                    }
                }
            }
        }


        /// <summary>
        /// Measures evenness of coverage, attempting to not be thrown off by changes due to CNVs.
        /// </summary>
        /// <param name="windowSize"></param>
        /// <returns>Typical evenness of a region of consistent copy number</returns>
        public double GetEvennessScore(int windowSize)
        {
            const double IQRthreshold = 0.015;
            const int windowSizeIQR = 10000;
            var evennessScoresIQR = reportScoresByWindow(windowSizeIQR);
            var quartiles = CanvasCommon.Utilities.Quartiles(evennessScoresIQR.Select(Convert.ToSingle).ToList());
            var evennessScores = reportScoresByWindow(windowSize);
            double median = CanvasCommon.Utilities.Median(evennessScores.ToList());
            return (quartiles.Item3 - quartiles.Item1 > IQRthreshold) ? quartiles.Item3 * 100.0 : median * 100.0;
        }

        /// <summary>
        /// Implements evenness score from https://academic.oup.com/nar/article-lookup/doi/10.1093/nar/gkq072#55451628
        /// on windows of coverage bins
        /// </summary>
        /// <returns>collection of per-window evenness scores</returns>
        private ConcurrentBag<double> reportScoresByWindow(int windowSize)
        {
            var evennessScores = new ConcurrentBag<double>();
            var tasks = CoverageInfo.CoverageByChr.Select(coverage => new ThreadStart(() =>
            {
                for (var index = 0; index < coverage.Value.Length - windowSize; index += windowSize)
                {
                    var tmp = coverage.Value.Skip(index).Take(windowSize - 1).ToList();
                    double average = tmp.Average();
                    double tmpEvenness = 0;
                    for (var coverageBin = 0; coverageBin <= average; coverageBin++)
                    {
                        tmpEvenness += tmp.Select(bin => bin >= coverageBin).Count(c => c) / tmp.Sum();
                    }

                    if (!double.IsInfinity(tmpEvenness) && !double.IsNaN(tmpEvenness))
                        evennessScores.Add(tmpEvenness);
                }
            })).ToList();
            Parallel.ForEach(tasks, task => task.Invoke());
            return evennessScores;
        }

        public double? GetCoverageVariability(int windowSize)
        {
            return GetCoverageVariability(windowSize, CoverageInfo.CoverageByChr);
        }

        /// <summary>
        /// Estimates coverage variability including waviness, attempting to not be thrown off by changes due to CNVs.
        /// </summary>
        /// <param name="windowSize"></param>
        /// <returns>Typical variability of a large region of consistent copy number</returns>
        public static double? GetCoverageVariability(int windowSize, Dictionary<string, double[]> dataByChr)
        {
            if (dataByChr.Select(coverage => coverage.Value.Count()).Sum() < 10 * windowSize)
                return null;
            const int windowSizeIQR = 10000;
            List<float> regionalVariability;
            if (windowSize > windowSizeIQR)
            {
                const double IQRthreshold = 0.015;
                regionalVariability = reportVariabilityByWindow(windowSizeIQR, dataByChr);
                var quartiles = CanvasCommon.Utilities.Quartiles(regionalVariability);
                // if the spread is large enough, it may be due to lots of CNVs; return a conservative value
                if ((quartiles.Item3 - quartiles.Item1) / quartiles.Item2 > IQRthreshold)
                    return quartiles.Item1;
            }
            // otherwise, assume the median for specified window size will be a good/safe measure
            regionalVariability = reportVariabilityByWindow(windowSize, dataByChr);
            return CanvasCommon.Utilities.Median(regionalVariability);
        }

        /// <summary>
        /// Computes MAD for each window of bin coverages and normalizes by window median value, yielding quasi-CV
        /// </summary>
        /// <returns></returns>
        private static List<float> reportVariabilityByWindow(int windowSize, Dictionary<string, double[]> dataByChr)
        {
            var regionalVariability = new ConcurrentBag<double>();
            var tasks = dataByChr.Select(coverage => new ThreadStart(() =>
            {
                for (var index = 0; index < coverage.Value.Length - windowSize; index += windowSize)
                {
                    var MAD = CanvasCommon.Utilities.Mad(coverage.Value, index, index + windowSize);
                    var median = CanvasCommon.Utilities.Median(coverage.Value, index, index + windowSize);
                    regionalVariability.Add(MAD / median);
                }
            })).ToList();
            Parallel.ForEach(tasks, task => task.Invoke());
            return regionalVariability.Select(Convert.ToSingle).ToList();
        }

        public List<double> FactorOfThreeCoverageVariabilities(int maxExponent = 8)
        {
            return FactorOfThreeCoverageVariabilities(CoverageInfo.CoverageByChr);
        }

        /// <summary>
        /// Compute the median of the local-median-normalized variability at various length scales.
        /// This is something like a coefficient of variation (CV) for each length scale;
        /// if two adjacent regions have median values that differ by some factor more than
        /// the value corresponding to the length of the shorter of the two regions, the difference
        /// may be considered significant.
        /// </summary>
        /// <param name="dataByChr"></param>
        /// <param name="maxExponent"></param>
        /// <returns></returns>
        public static List<double> FactorOfThreeCoverageVariabilities(Dictionary<string, double[]> dataByChr, int maxExponent = 8)
        {
            // Conceptual description:
            //
            // While a single variance/standard deviation/MAD value could be used on IID data (with the single-bin
            // standard deviation being scaled by 1/sqrt(n) for a segment of n bins, genomic coverage
            // data is not IID and exhibits variability on various scales.  This is similar to the distinction
            // between 'roughness' and 'waviness' in characterization of surface smoothness.  By measuring variability
            // at various length scales, we can get an empirical measure of the expected deviations at a given scale.
            // 
            // Here, we consider variability at length scales of 1, 3, 3*3, 3*3*3, ....  The choice of 3 is a matter of
            // convenience: At a given scale, we can take the middle value of three adjacent points as the median, and 
            // use the other two values to compute an average absolute deviation.  We use the middle values at one scale
            // at the next larger scale and repeat the process.

            var factorOfThreeCMADs = new List<double> { 0 };
            var results = new Dictionary<string, double[]>(dataByChr);
            int exponent = 1;
            while (exponent <= maxExponent)
            {
                var CMADs = new ConcurrentBag<Double>();
                var source = results;
                var tasks = source.Select(coverage => new ThreadStart(() =>
                {
                    var cmads = new List<double>();
                    results[coverage.Key] = GetTripletMediansAndCMADs(coverage.Value, CMADs);
                })).ToList();
                Parallel.ForEach(tasks, task => task.Invoke());
                var cmadList = CMADs.ToList();
                if (cmadList.Count < 50)
                {
                    factorOfThreeCMADs.AddRange(Enumerable.Repeat(factorOfThreeCMADs.Last(), maxExponent - factorOfThreeCMADs.Count + 1));
                    break;
                }
                factorOfThreeCMADs.Add(CanvasCommon.Utilities.Median(cmadList));
                ++exponent;
            }
            return factorOfThreeCMADs;
        }

        private static double[] GetTripletMediansAndCMADs(double[] data, ConcurrentBag<double> CMADs)
        {
            int L = data.Length;
            int n = L / 3;
            var tripletMedians = new double[n];
            // for window of three adjacent data points, find the median and compute the average absolute deviation from the median
            for (int i = 0; i < n; i++)
            {
                int j = i * 3 + 1;
                double a = data[j - 1];
                double b = data[j];
                double c = data[j + 1];

                // ensure that a <= b <= c
                if (a > b)
                    (b, a) = (a, b);
                if (a > c)
                    (c, a) = (a, c);
                if (b > c)
                    (c, b) = (b, c);

                tripletMedians[i] = b;
                CMADs.Add((c - a) / 2d / b);
            }
            return tripletMedians;
        }
    }
}
