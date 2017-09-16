﻿using System;
using System.Linq;
using System.Threading;
using DynamixelServo.Driver;

namespace DynamixelServo.Quadruped
{
    class QuadrupedInterpolationDriver : IDisposable
    {
        #region quadrupedConstants

        // angles by which the legs are ofcenter from the motor centers
        private const int FemurOffset = 13;
        private const int TibiaOffset = 35;

        // Correction numbers are set up so that if you add them to the angle the motor center will bo pointing to the desired angle
        // the offset for each axis is than added to compensate for the shape of the legs
        private static readonly LegConfiguration FrontLeft = new LegConfiguration(1, 3, 5, 45, new Vector3(-6.5f, 6.5f, 0), -240 + FemurOffset, -330 + TibiaOffset);
        private static readonly LegConfiguration FrontRight = new LegConfiguration(2, 4, 6, -45, new Vector3(6.5f, 6.5f, 0), 60 + FemurOffset, -30 + TibiaOffset);
        private static readonly LegConfiguration RearLeft = new LegConfiguration(7, 9, 11, 135, new Vector3(-6.5f, -6.5f, 0), 60 + FemurOffset, -30 + TibiaOffset);
        private static readonly LegConfiguration RearRight = new LegConfiguration(8, 10, 12, -135, new Vector3(6.5f, -6.5f, 0), -240 + FemurOffset, -330 + TibiaOffset);

        private static readonly byte[] Coxas = { FrontLeft.CoxaId, FrontRight.CoxaId, RearLeft.CoxaId, RearRight.CoxaId };
        private static readonly byte[] Femurs = { FrontLeft.FemurId, FrontRight.FemurId, RearLeft.FemurId, RearRight.FemurId };
        private static readonly byte[] Tibias = { FrontLeft.TibiaId, FrontRight.TibiaId, RearLeft.TibiaId, RearRight.TibiaId };

        private static readonly byte[] AllMotorIds = new[] { Coxas, Femurs, Tibias }.SelectMany(x => x).ToArray();

        private const float CoxaLength = 5.3f;
        private const float FemurLength = 6.5f;
        private const float TibiaLength = 13f;

        #endregion

        private readonly DynamixelDriver _driver;

        private const double Speed = 2; // speed in cm per second
        private const int updateFrequency = 30;
        private TimeSpan _frameLength = TimeSpan.FromMilliseconds(1000.0 / updateFrequency);

        private readonly Thread _interpolationThread;

        private Vector3 FrontLeftTarget;
        private Vector3 FrontRightTarget;
        private Vector3 RearLeftTarget;
        private Vector3 RearRightTarget;

        private Vector3 _frontLeftLastWrittenTarget;
        private Vector3 _frontRightLastWrittenTarget;
        private Vector3 _rearLeftLastWrittenTarget;
        private Vector3 _rearRightLastWrittenTarget;

        public QuadrupedInterpolationDriver(DynamixelDriver driver)
        {
            _driver = driver;
            _interpolationThread = new Thread(Interpolate)
            {
                IsBackground = true,
                Name = "Quadruped Interpolation Thread"
            };
            _interpolationThread.Start();
        }

        private void Interpolate()
        {
            while (true)
            {
                CompareAndMove(ref _frontLeftLastWrittenTarget, FrontLeftTarget, FrontLeft);
                CompareAndMove(ref _frontRightLastWrittenTarget, FrontRightTarget, FrontRight);
                CompareAndMove(ref _rearLeftLastWrittenTarget, RearLeftTarget, RearLeft);
                CompareAndMove(ref _rearRightLastWrittenTarget, RearRightTarget, RearRight);
                Thread.Sleep(_frameLength);
            }
        }

        private void CompareAndMove(ref Vector3 lastWrittenTarget, Vector3 target, LegConfiguration config)
        {
            if (Vector3.Similar(lastWrittenTarget, target))
            {
                return;
            }
            // A to B vector is B - A
            Vector3 translationVector = (target - lastWrittenTarget).Normal * (float)(Speed / _frameLength.TotalSeconds);
            Vector3 nextSteptarget = translationVector + lastWrittenTarget;
            MoveLeg(nextSteptarget, config);
            lastWrittenTarget = nextSteptarget;
        }

        private void MoveLeg(Vector3 target, LegConfiguration legConfig)
        {
            var legGoalPositions = CalculateIkForLeg(target, legConfig);
            _driver.SetGoalPositionInDegrees(legConfig.CoxaId, legGoalPositions.Coxa);
            _driver.SetGoalPositionInDegrees(legConfig.FemurId, legGoalPositions.Femur);
            _driver.SetGoalPositionInDegrees(legConfig.TibiaId, legGoalPositions.Tibia);
        }

        public void Dispose()
        {
            _interpolationThread.Abort();
            _driver?.Dispose();
        }

        #region IkFunctions

        private static Vector3 CalculateFkForLeg(LegGoalPositions currentPsoitions, LegConfiguration legConfig)
        {
            float femurAngle = Math.Abs(currentPsoitions.Femur - Math.Abs(legConfig.FemurCorrection));
            float tibiaAngle = Math.Abs(currentPsoitions.Tibia - Math.Abs(legConfig.TibiaCorrection));
            float coxaAngle = 150 - currentPsoitions.Coxa - legConfig.AngleOffset;
            float baseX = (float)Math.Sin(coxaAngle.DegreeToRad());
            float baseY = (float)Math.Cos(coxaAngle.DegreeToRad());
            Vector3 coxaVector = new Vector3(baseX, baseY, 0) * CoxaLength;
            float femurX = (float)Math.Sin((femurAngle - 90).DegreeToRad()) * FemurLength;
            float femurY = (float)Math.Cos((femurAngle - 90).DegreeToRad()) * FemurLength;
            Vector3 femurVector = new Vector3(baseX * femurY, baseY * femurY, femurX);
            // to calculate tibia we need angle between tibia and a vertical line
            // we get this by calculating the angles formed by a horizontal line from femur, femur and part of fibia by knowing that the sum of angles is 180
            // than we just remove this from teh tibia andgle and done
            float angleForTibiaVector = tibiaAngle - (180 - 90 - (femurAngle - 90));
            float tibiaX = (float)Math.Sin(angleForTibiaVector.DegreeToRad()) * TibiaLength;
            float tibiaY = (float)Math.Cos(angleForTibiaVector.DegreeToRad()) * TibiaLength;
            Vector3 tibiaVector = new Vector3(baseX * tibiaX, baseY * tibiaX, -tibiaY);
            return legConfig.CoxaPosition + coxaVector + femurVector + tibiaVector;
        }

        private static LegGoalPositions CalculateIkForLeg(Vector3 target, LegConfiguration legConfig)
        {
            Vector3 relativeVector = target - legConfig.CoxaPosition;
            float targetAngle = (float)(Math.Atan2(relativeVector.X, relativeVector.Y).RadToDegree() + legConfig.AngleOffset);

            float horizontalDistanceToTarget = (float)Math.Sqrt(Math.Pow(relativeVector.X, 2) + Math.Pow(relativeVector.Y, 2));
            float horizontalDistanceWithoutCoxa = horizontalDistanceToTarget - CoxaLength;
            float absoluteDistanceToTargetWithoutCoxa = (float)Math.Sqrt(Math.Pow(horizontalDistanceWithoutCoxa, 2) + Math.Pow(relativeVector.Z, 2));
            // use sss triangle solution to calculate angles
            // use law of cosinus to get angles in two corners
            float angleByTibia = (float)GetAngleByA(absoluteDistanceToTargetWithoutCoxa, FemurLength, TibiaLength);
            float angleByFemur = (float)GetAngleByA(TibiaLength, FemurLength, absoluteDistanceToTargetWithoutCoxa);
            // we have angles of the SSS trianglel. now we need angle for the servos
            float groundToTargetAngleSize = (float)Math.Atan2(horizontalDistanceWithoutCoxa, -relativeVector.Z).RadToDegree();
            if (targetAngle >= 90 || targetAngle <= -90)
            {
                // target is behind me
                // can still happen if target is right bellow me
                throw new NotSupportedException($"Target angle is {targetAngle}");
            }
            float femurAngle = angleByFemur + groundToTargetAngleSize;
            // these angles need to be converted to the dynamixel angles
            // in other words horizon is 150 for the dynamixel angles so we need to recalcualate them
            float correctedFemur = Math.Abs(legConfig.FemurCorrection + femurAngle);
            float correctedTibia = Math.Abs(legConfig.TibiaCorrection + angleByTibia);
            float correctedCoxa = 150f - targetAngle;
            return new LegGoalPositions(correctedCoxa, correctedFemur, correctedTibia);
        }

        private static double GetAngleByA(double a, double b, double c)
        {
            return Math.Acos((b.ToPower(2) + c.ToPower(2) - a.ToPower(2)) / (2 * b * c)).RadToDegree();
        }

        #endregion

    }
}