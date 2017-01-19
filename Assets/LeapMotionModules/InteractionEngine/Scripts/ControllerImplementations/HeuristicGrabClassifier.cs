﻿using UnityEngine;
using System;
using System.Collections.Generic;
using Leap.Unity.RuntimeGizmos;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Leap.Unity.Interaction {
  public class HeuristicGrabClassifier : IHandModel, IRuntimeGizmoComponent {
    public bool TwoHandedGrab = false;
    Collider[] collidingCandidates = new Collider[10];

    //Grab Classifier Logic
    public void UpdateClassifier(GrabClassifier classifier) {
      classifier.warpTrans = Matrix4x4.TRS(classifier.warper.RigidbodyPosition, classifier.warper.RigidbodyRotation, classifier.transform.localScale);
      
      //For each probe (fingertip)
      for (int j = 0; j < classifier.probes.Length; j++) {
        //Calculate how extended the finger is
        float tempCurl = Vector3.Dot(_hand.Fingers[j].Direction.ToVector3(), (j != 0) ? _hand.Direction.ToVector3() : (_hand.IsLeft ? 1f : -1f) * _hand.Basis.xBasis.ToVector3());

        //Determine if this probe is intersecting an object
        Array.Clear(collidingCandidates, 0, 10);
        Physics.OverlapSphereNonAlloc(_hand.Fingers[j].TipPosition.ToVector3(), j == 0 ? 0.015f : 0.01f, collidingCandidates);
        bool collidingWithObject = false;
        foreach(Collider col in collidingCandidates) { collidingWithObject = (col == classifier.collider) ? true : collidingWithObject; }

        //Nullify above findings if fingers are extended
        bool tempIsInside = collidingWithObject && (tempCurl < 0.65f);

        //Probes go inside when they intersect, probes come out when they uncurl
        if (!classifier.probes[j].isInside) {
          classifier.probes[j].isInside = tempIsInside;
          classifier.probes[j].curl = tempCurl + (j==0?0.15f:0f);
        } else {
          if (tempCurl > classifier.probes[j].curl) {
            classifier.probes[j].isInside = tempIsInside;
          }
        }
      }

      //If thumb and one other finger is "inside" the object, it's a grab!
      classifier.isGrabbing = (classifier.probes[0].isInside && (classifier.probes[1].isInside ||
                                                                 classifier.probes[2].isInside ||
                                                                 classifier.probes[3].isInside ||
                                                                 classifier.probes[4].isInside));
      //If grabbing for 2 frames, truly register it as a grab.
      //Suppresses spurious regrabs and makes throws work better.
      if (classifier.isGrabbing) {
        if (classifier.warmUp <= 2) {
          classifier.warmUp++;
          classifier.isGrabbing = false;
        }
      } else {
        classifier.warmUp = 0;
      }
    }

    Dictionary<IInteractionBehaviour, GrabClassifier> grabClassifiers = new Dictionary<IInteractionBehaviour, GrabClassifier>();
    public void UpdateBehaviour(IInteractionBehaviour behaviour) {
      GrabClassifier classifier;
      if (!grabClassifiers.TryGetValue(behaviour, out classifier)) {
        classifier = new GrabClassifier(behaviour);
        grabClassifiers.Add(behaviour, classifier);
      }

      //Do the actual grab classification logic
      UpdateClassifier(classifier);

      if (classifier.isGrabbing != classifier.prevGrabbing) {
        if (classifier.isGrabbing) {
          if (!TwoHandedGrab) { _manager.ReleaseObject(behaviour); }
          _manager.GraspWithHand(_hand, behaviour);
        } else if (!classifier.isGrabbing || behaviour.IsBeingGraspedByHand(_hand.Id)) {
          _manager.ReleaseHand(_hand.Id);
        }
      }
      classifier.prevGrabbing = classifier.isGrabbing;
    }

    //Classifier bookkeeping
    public void Update() {
      if (gameObject.activeInHierarchy && _hand != null) {

        //First check if already holding an object and only process that one
        bool alreadyGrasping = false;
        var graspedBehaviours = _manager.GraspedObjects;
        for (int i = 0; i < graspedBehaviours.Count; i++) {
          if (graspedBehaviours[i].IsBeingGraspedByHand(_hand.Id)) {
            UpdateBehaviour(graspedBehaviours[i]);
            alreadyGrasping = true;
            break;
          }
        }

        //If not, process all objects
        if (!alreadyGrasping) {
          var activeBehaviours = _manager._activityManager.ActiveBehaviours;
          for (int i = 0; i < activeBehaviours.Count; i++) {
            UpdateBehaviour(activeBehaviours[i]);
          }
        }
      }
    }

    //Per-Object Per-Hand Classifier
    public class GrabClassifier {
      public bool isGrabbing;
      public bool prevGrabbing;
      public GrabProbe[] probes = new GrabProbe[5];
      public Transform transform;
      public Collider collider;
      public RigidbodyWarper warper;
      public Matrix4x4 warpTrans;
      public int warmUp;

      public GrabClassifier(IInteractionBehaviour behaviour) {
        probes = new GrabProbe[5];
        transform = behaviour.transform;
        collider = behaviour.GetComponentInChildren<Collider>();
        warper = (behaviour as InteractionBehaviour).warper;
        warmUp = 0;
      }
    }

    //Per-Finger Per-Object Probe
    public struct GrabProbe {
      public bool isInside;
      public float curl;
    }

    //Draw Debug Telemetry
    public void OnDrawRuntimeGizmos(RuntimeGizmoDrawer drawer) {
      if (gameObject.activeInHierarchy && _hand != null) {
        for (var it = grabClassifiers.GetEnumerator(); it.MoveNext();) {
          bool isActive = false;
          for (int i = 0; i < _manager._activityManager.ActiveBehaviours.Count; i++) {
            if (_manager._activityManager.ActiveBehaviours[i] == it.Current.Key) {
              isActive = true;
              break;
            }
          }
          if (isActive) {
            for (int i = 0; i < it.Current.Value.probes.Length; i++) {
              if (it.Current.Value.probes[i].isInside) {
                drawer.color = Color.red;
                drawer.DrawSphere(_hand.Fingers[i].TipPosition.ToVector3(), i == 0 ? 0.015f : 0.01f);
              }
            }
          }
        }
      }
    }

    //HAND INTERFACE
    //-------------------------------------------------------------------
    public InteractionManager _manager;
    private Hand _hand;

    /** The model type. An InteractionBrushHand is a type of physics model. */
    public override ModelType HandModelType {
      get { return ModelType.Physics; }
    }

    [SerializeField]
    private Chirality handedness;
    /** Whether this model can be used to represent a right or a left hand.*/
    public override Chirality Handedness {
      get { return handedness; }
      set { handedness = value; }
    }

    /** Gets the Leap.Hand object whose data is used to update this model. */
    public override Hand GetLeapHand() { return _hand; }
    /** Sets the Leap.Hand object to use to update this model. */
    public override void SetLeapHand(Hand hand) { _hand = hand; }

    /** Initializes this hand model. */
    public override void InitHand() {
      base.InitHand();
      if (Application.isPlaying) {
        if (_manager == null) {
          _manager = FindObjectOfType<InteractionManager>();
        }
        gameObject.layer = _manager.InteractionBrushLayer;
      }
    }

    /** Updates this hand model. */
    public override void UpdateHand() {
#if UNITY_EDITOR
      if (!EditorApplication.isPlaying) {
        return;
      }
#endif
    }

    public override void BeginHand() {
      base.BeginHand();
      gameObject.SetActive(true);
    }

    public override void FinishHand() {
      gameObject.SetActive(false);
      base.FinishHand();
    }
  }
}