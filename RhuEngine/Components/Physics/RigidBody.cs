﻿using RhuEngine.WorldObjects;
using RhuEngine.WorldObjects.ECS;

using RNumerics;
using RhuEngine.Linker;
using RhuEngine.Physics;
using System;

namespace RhuEngine.Components
{
	[Category(new string[] { "Physics" })]
	[UpdateLevel(UpdateEnum.Movement)]
	public sealed class RigidBody : Component
	{
		public readonly Linker<Vector3f> Position;

		public readonly Linker<Quaternionf> Rotation;

		public readonly Linker<Vector3f> Scale;

		[OnChanged(nameof(EntityChanged))]
		public readonly SyncRef<Entity> TargetEntity;

		[Default(true)]
		public readonly Sync<bool> PausePhysicsOnGrab;

		protected override void OnAttach() {
			base.OnAttach();
			TargetEntity.Target = Entity;
			PhysicsObject.Target = Entity.GetFirstComponent<PhysicsObject>();
		}

		private void EntityChanged() {
			if(TargetEntity.Target is null) {
				return;
			}
			if (Scale.Target is null) {
				Scale.Target = TargetEntity.Target.scale;
			}
			if (Rotation.Target is null) {
				Rotation.Target = TargetEntity.Target.rotation;
			}
			if (Position.Target is null) {
				Position.Target = TargetEntity.Target.position;
			}
		}

		[Default(10f)]
		[OnChanged(nameof(MassChanged))]
		public readonly Sync<float> Mass;

		private void MassChanged() {
			var rig = _physicsObject?.rigidBody;
			if(rig is null) {
				return;
			}
			rig.Mass = Mass;
		}

		[OnChanged(nameof(PhysicsObjectChanged))]
		public readonly SyncRef<PhysicsObject> PhysicsObject;

		[NoLoad]
		[NoSave]
		[NoSync]
		private PhysicsObject _physicsObject;

		protected override void OnLoaded() {
			base.OnLoaded();
			Entity.OnGrabbed += Entity_OnGrabbed;
			Entity.OnDroped += Entity_OnDroped;
			PhysicsObjectChanged();
		}

		private void Entity_OnDroped() {
			if (_physicsObject is null) {
				return;
			}
			if (PausePhysicsOnGrab.Value) {
				_physicsObject.rigidBody.Activate = true;
			}
		}

		private void Entity_OnGrabbed(Grabbable obj) {
			if(_physicsObject is null) {
				return;
			}
			if (PausePhysicsOnGrab.Value) {
				_physicsObject.rigidBody.Activate = false;
			}
		}

		private void PhysicsObjectChanged() {
			if(_physicsObject == PhysicsObject.Target) {
				return;
			}
			if(_physicsObject is not null) {
				_physicsObject.rigidBody.NoneStaticBody = false;
				_physicsObject.rigidBody.Mass = 0f;
				_physicsObject.AddedData -= PhysicsObject_AddedData;
			}
			_physicsObject = PhysicsObject.Target;
			if (_physicsObject is null) {
				return;
			}
			_physicsObject.AddedData += PhysicsObject_AddedData;
			PhysicsObject_AddedData(_physicsObject.rigidBody);
		}

		private void PhysicsObject_AddedData(RigidBodyCollider obj) {
			if(obj is null) {
				return;
			}
			obj.NoneStaticBody = true;
			obj.Mass = Mass;
			obj.Enabled = true;
		}

		protected override void Step() {
			base.Step();
			var colider = PhysicsObject.Target?.rigidBody;
			if (colider is null) {
				return;
			}
			if (!colider.Activate) {
				return;
			}
			var entity = TargetEntity.Target;
			if (entity is null) {
				return;
			}
			if (!entity.position.IsLinkedTo) {
				return;
			}
			if (!entity.rotation.IsLinkedTo) {
				return;
			}
			if (!entity.scale.IsLinkedTo) {
				return;
			}
			entity.SetGlobalMatrixPysics(colider.Matrix);
		}
	}
}
