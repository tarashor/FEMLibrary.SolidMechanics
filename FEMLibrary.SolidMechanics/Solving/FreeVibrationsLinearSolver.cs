﻿#define ALL_LAMBDAS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FEMLibrary.SolidMechanics.Physics;
using FEMLibrary.SolidMechanics.Meshing;
using MatrixLibrary;
using FEMLibrary.SolidMechanics.Geometry;
using FEMLibrary.SolidMechanics.Utils;
using FEMLibrary.SolidMechanics.NumericalUtils;
using System.Diagnostics;
using FEMLibrary.SolidMechanics.Results;

namespace FEMLibrary.SolidMechanics.Solving
{
    public class FreeVibrationsLinearSolver:Solver
    {
        public FreeVibrationsLinearSolver(Model model, Mesh mesh, double error)
            : base(model, mesh)
        {
            _error = error;
        }


        private double _error;
        private Matrix ConstMatrix;

        private double M1;
        private double M2;
        private double M3;
        private double G13;

        IFiniteElement elementCurrent;
        List<int> indeciesToDelete;

        public override IEnumerable<INumericalResult> Solve(int resultsCount)
        {
            List<INumericalResult> results = new List<INumericalResult>();
            
            if (_mesh.IsMeshGenerated)
            {
                GetConstantMatrix();

                Matrix StiffnessMatrix = GetStiffnessMatrix();
                StiffnessMatrix = AsumeStaticBoundaryConditions(StiffnessMatrix);

#if (ALL_LAMBDAS)
                Vector[] eigenVectors;
                int iterations;
                //Vector lambdas = StiffnessMatrix.GetEigenvalues(out eigenVectors, out iterations, _error);
                double[] lambdas = StiffnessMatrix.GetEigenvalueSPAlgorithm(out eigenVectors, _error, resultsCount);
                //Debug.WriteLine(iterations);

                for (int i = 0; i < lambdas.Length; i++)
                {
                    Vector eigenVector = eigenVectors[i];
                    addStaticPoints(eigenVector);
                    EigenValuesNumericalResult result = new EigenValuesNumericalResult(_mesh.Elements, eigenVector, Math.Sqrt(lambdas[i] / _model.Material.Rho));
                    results.Add(result);
                }
#else
                Vector eigenVector;
                double lambda = StiffnessMatrix.GetMaxEigenvalueSPAlgorithm(out eigenVector, _error);
                addStaticPoints(eigenVector);

                EigenValuesNumericalResult result = new EigenValuesNumericalResult(_mesh.Elements, eigenVector, Math.Sqrt(lambda / _model.Material.Rho));
                results.Add(result);
#endif
            }
            return results;
        }

        private Matrix GetConstantMatrix()
        {
            ConstMatrix = new Matrix(4, 4);
            M1 = ConstMatrix[0, 0] = _model.Material.GetE1Modif() * (1 + _model.Material.GetAlfa1());
            M2 = ConstMatrix[1, 0] = ConstMatrix[0, 1] = _model.Material.GetLambda1() * _model.Material.GetE0();
            M3 = ConstMatrix[1, 1] = _model.Material.GetE0();
            G13 = ConstMatrix[2, 2] = ConstMatrix[2, 3] = ConstMatrix[3, 2] = ConstMatrix[3, 3] = _model.Material.GetG13();
            return ConstMatrix;
        }

        #region Boundary conditions
        //static == fixed
        private Matrix AsumeStaticBoundaryConditions(Matrix stiffnessMatrix)
        {
            indeciesToDelete = new List<int>();

            foreach (Edge edge in _model.Shape.Edges)
            {
                BoundaryCondition boundaryCondition = _model.BoundaryConditions[edge];
                if (boundaryCondition.Type == BoundaryConditionsType.Static)
                {
                    IEnumerable<FiniteElementNode> nodes = _mesh.GetNodesOnEdge(edge);
                    foreach (FiniteElementNode node in nodes)
                    {
                        if (!indeciesToDelete.Contains(2 * node.Index))
                        {
                            indeciesToDelete.Add(2 * node.Index);
                        }
                        if (!indeciesToDelete.Contains(2 * node.Index + 1))
                        {
                            indeciesToDelete.Add(2 * node.Index + 1);
                        }
                    }
                }
            }
            

            foreach (Point point in _model.Shape.Points)
            {
                BoundaryCondition pointCondition = _model.PointConditions[point];
                if (pointCondition.Type == BoundaryConditionsType.Static)
                {
                    FiniteElementNode node = _mesh.GetNodeOnPoint(point);
                    if (node != null)
                    {
                        if (!indeciesToDelete.Contains(2 * node.Index))
                        {
                            indeciesToDelete.Add(2 * node.Index);
                        }
                        if (!indeciesToDelete.Contains(2 * node.Index + 1))
                        {
                            indeciesToDelete.Add(2 * node.Index + 1);
                        }
                    }
                }
            }

            
            indeciesToDelete.Sort();
            indeciesToDelete.Reverse();

            foreach (int index in indeciesToDelete) {
                stiffnessMatrix = Matrix.Cut(stiffnessMatrix, index, index);
            }

            return stiffnessMatrix;
        }

        private void addStaticPoints(Vector resultVector)
        {
            indeciesToDelete.Sort();
            foreach (int index in indeciesToDelete)
            {
                resultVector.Insert(0, index);
            }
        }
        #endregion


        #region StiffnessMatrix

        private Matrix GetStiffnessMatrix()
        {
            Matrix StiffnessMatrix = new Matrix(_mesh.Nodes.Count * 2, _mesh.Nodes.Count * 2);
            foreach (IFiniteElement element in _mesh.Elements)
            {
                Matrix localStiffnessMatrix = GetLocalStiffnessMatrix(element);

                for (int i = 0; i < element.Count; i++)
                {
                    for (int j = 0; j < element.Count; j++)
                    {
                        StiffnessMatrix[2 * element[i].Index, 2 * element[j].Index] += localStiffnessMatrix[2 * i, 2 * j];
                        StiffnessMatrix[2 * element[i].Index + 1, 2 * element[j].Index] += localStiffnessMatrix[2 * i + 1, 2 * j];
                        StiffnessMatrix[2 * element[i].Index, 2 * element[j].Index + 1] += localStiffnessMatrix[2 * i, 2 * j + 1];
                        StiffnessMatrix[2 * element[i].Index + 1, 2 * element[j].Index + 1] += localStiffnessMatrix[2 * i + 1, 2 * j + 1];
                    }
                }
            }

            return StiffnessMatrix;
        }



        #region Local Matrix

        private Matrix GetLocalStiffnessMatrix(IFiniteElement element)
        {
            elementCurrent = element;

            Matrix localStiffnessMatrix = Integration.GaussianIntegrationMatrix(LocalStiffnessMatrixFunction);

            return localStiffnessMatrix;
        }

        private Matrix LocalStiffnessMatrixFunction(double ksi, double eta)
        {
            Matrix derivativeMatrix = GetLocalDerivativeMatrix(elementCurrent, ksi, eta);

            JacobianRectangular J = new JacobianRectangular();
            J.Element = elementCurrent;

            return derivativeMatrix * ConstMatrix * Matrix.Transpose(derivativeMatrix) * J.GetJacobianDeterminant(ksi, eta);
        }

        private Matrix GetLocalDerivativeMatrix(IFiniteElement element, double ksi, double eta)
        {
            Matrix LocalDerivativeMatrix = new Matrix(8, 4);

            Matrix gradNksieta = new Matrix(2, 4);

            gradNksieta[0, 0] = (eta - 1) * 0.25;
            gradNksieta[1, 0] = (ksi - 1) * 0.25;
            gradNksieta[0, 1] = (1 - eta) * 0.25;
            gradNksieta[1, 1] = (-ksi - 1) * 0.25;
            gradNksieta[0, 2] = (eta + 1) * 0.25;
            gradNksieta[1, 2] = (ksi + 1) * 0.25;
            gradNksieta[0, 3] = (-eta - 1) * 0.25;
            gradNksieta[1, 3] = (1 - ksi) * 0.25;

            JacobianRectangular J = new JacobianRectangular();
            J.Element = element;

            Matrix gradN = J.GetInverseJacobian(ksi, eta) * gradNksieta;

            LocalDerivativeMatrix[0, 0] = LocalDerivativeMatrix[1, 3] = gradN[0, 0];
            LocalDerivativeMatrix[2, 0] = LocalDerivativeMatrix[3, 3] = gradN[0, 1];
            LocalDerivativeMatrix[4, 0] = LocalDerivativeMatrix[5, 3] = gradN[0, 2];
            LocalDerivativeMatrix[6, 0] = LocalDerivativeMatrix[7, 3] = gradN[0, 3];

            LocalDerivativeMatrix[1, 1] = LocalDerivativeMatrix[0, 2] = gradN[1, 0];
            LocalDerivativeMatrix[3, 1] = LocalDerivativeMatrix[2, 2] = gradN[1, 1];
            LocalDerivativeMatrix[5, 1] = LocalDerivativeMatrix[4, 2] = gradN[1, 2];
            LocalDerivativeMatrix[7, 1] = LocalDerivativeMatrix[6, 2] = gradN[1, 3];

            return LocalDerivativeMatrix;
        }
        #endregion

        #endregion

    }
}
