﻿using System;
using System.Collections.Generic;
using CanvasCommon;
using Isas.Framework.DataTypes.Maps;

namespace CanvasPedigreeCaller
{
    internal class HaplotypeVariantCaller : IVariantCaller
    {
        private readonly List<Genotype> _alleleCopyNumberGenotypes;
        private readonly PedigreeCallerParameters _callerParameters;

        public HaplotypeVariantCaller(PedigreeCallerParameters callerParameters)
        {
            _alleleCopyNumberGenotypes = GenerateGenotypeCombinations(callerParameters.MaximumCopyNumber);
            _callerParameters = callerParameters;
        }

        public void CallVariant(ISampleMap<CanvasSegment> canvasSegments, ISampleMap<SampleMetrics> samplesInfo,
            ISampleMap<CopyNumberModel> copyNumberModel,
            PedigreeInfo pedigreeInfo)
        {
            Console.Out.WriteLine(_alleleCopyNumberGenotypes);
            Console.Out.WriteLine(_callerParameters);
            throw new NotImplementedException();
        }

        /// <summary>
        /// Generate all possible copy number genotype combinations with the maximal number of alleles per segment set to maxAlleleNumber.
        /// </summary>
        /// <param name="numberOfCnStates"></param>
        /// <returns> </returns>
        private static List<Genotype> GenerateGenotypeCombinations(int numberOfCnStates)
        {
            var genotypes = new List<Genotype>();
            for (int cn = 0; cn < numberOfCnStates; cn++)
            {
                for (int gt = 0; gt <= cn; gt++)
                {
                    genotypes.Add(new Genotype(gt, cn - gt));
                }
            }
            return genotypes;
        }
    }
}