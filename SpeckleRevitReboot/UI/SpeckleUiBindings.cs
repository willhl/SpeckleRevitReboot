﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using SpeckleCore;
using SpeckleRevit.Storage;
using SpeckleUiBase;

namespace SpeckleRevit.UI
{
  /// <summary>
  /// Handles most of the revit specific implementations for any speckle-derived actions.
  /// </summary>
  public partial class SpeckleUiBindingsRevit : SpeckleUIBindings
  {
    public static UIApplication RevitApp;
    public static UIDocument CurrentDoc { get => RevitApp.ActiveUIDocument; }

    /// <summary>
    /// Stores the actions for the ExternalEvent handler
    /// </summary>
    public List<Action> Queue;

    public ExternalEvent Executor;

    public Timer SelectionTimer;

    /// <summary>
    /// Holds the current project's clients
    /// </summary>
    public SpeckleClientsWrapper ClientListWrapper;

    public List<SpeckleStream> LocalState;

    public SpeckleUiBindingsRevit(UIApplication _RevitApp) : base()
    {
      RevitApp = _RevitApp;
      Queue = new List<Action>();
      ClientListWrapper = new SpeckleClientsWrapper();
    }

    /// <summary>
    /// Sets the revit external event handler and intialises the rocket enginges.
    /// </summary>
    /// <param name="eventHandler"></param>
    public void SetExecutorAndInit(ExternalEvent eventHandler)
    {
      Executor = eventHandler;

      // LOCAL STATE
      LocalState = new List<SpeckleStream>();
      Queue.Add(new Action(() =>
    {
      using (Transaction t = new Transaction(CurrentDoc.Document, "Switching Local Speckle State"))
      {
        t.Start();
        LocalState = SpeckleStateManager.ReadState(CurrentDoc.Document);
        InjectStateInKits();
        t.Commit();
      }
    }));
      Executor.Raise();

      // REVIT INJECTION
      InjectRevitAppInKits();

      // GLOBAL EVENT HANDLERS
      RevitApp.ViewActivated += RevitApp_ViewActivated;
      RevitApp.Application.DocumentChanged += Application_DocumentChanged;
      RevitApp.Application.DocumentOpened += Application_DocumentOpened;
      RevitApp.Application.DocumentClosed += Application_DocumentClosed;
      RevitApp.Idling += ApplicationIdling;


      SelectionTimer = new Timer(1400) { AutoReset = true, Enabled = true };
      SelectionTimer.Elapsed += SelectionTimer_Elapsed;
      // TODO: Find a way to handle when document is closed via middle mouse click
      // thus triggering the focus on a new project

    }

    private void SelectionTimer_Elapsed(object sender, ElapsedEventArgs e)
    {
      if (CurrentDoc == null) return;
      var selectedObjectsCount = CurrentDoc != null ? CurrentDoc.Selection.GetElementIds().Count : 0;

      NotifyUi("update-selection-count", JsonConvert.SerializeObject(new
      {
        selectedObjectsCount
      }));
    }

    private void ApplicationIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {
    }

    #region Kit injection utils

    /// <summary>
    /// Injects the Revit app in any speckle kit Initialiser class that has a 'RevitApp' property defined. This is need for creating revit elements from that assembly without having hard references on the ui library.
    /// </summary>
    public void InjectRevitAppInKits()
    {
      var assemblies = SpeckleCore.SpeckleInitializer.GetAssemblies();
      foreach (var ass in assemblies)
      {
        var types = ass.GetTypes();
        foreach (var type in types)
        {
          if (type.GetInterfaces().Contains(typeof(SpeckleCore.ISpeckleInitializer)))
          {
            if (type.GetProperties().Select(p => p.Name).Contains("RevitApp"))
            {
              type.GetProperty("RevitApp").SetValue(null, RevitApp);
            }
          }
        }
      }
    }

    /// <summary>
    /// Injects the current lolcal state in any speckle kit initialiser class that has a "LocalRevitState" property defined. 
    /// This can then be used to determine what existing speckle baked objects exist in the current doc and either modify/delete whatever them in the conversion methods.
    /// </summary>
    public void InjectStateInKits()
    {
      var assemblies = SpeckleCore.SpeckleInitializer.GetAssemblies();
      foreach (var ass in assemblies)
      {
        var types = ass.GetTypes();
        foreach (var type in types)
        {
          if (type.GetInterfaces().Contains(typeof(SpeckleCore.ISpeckleInitializer)))
          {
            if (type.GetProperties().Select(p => p.Name).Contains("LocalRevitState"))
            {
              type.GetProperty("LocalRevitState").SetValue(null, LocalState);
            }
          }
        }
      }
    }

    /// <summary>
    /// Injects a scale property to be used in conversion methods if needed.
    /// </summary>
    public void InjectScaleInKits(double scale)
    {
      var assemblies = SpeckleCore.SpeckleInitializer.GetAssemblies();
      foreach (var ass in assemblies)
      {
        var types = ass.GetTypes();
        foreach (var type in types)
        {
          if (type.GetInterfaces().Contains(typeof(SpeckleCore.ISpeckleInitializer)))
          {
            if (type.GetProperties().Select(p => p.Name).Contains("RevitScale"))
            {
              type.GetProperty("RevitScale").SetValue(null, scale);
            }
          }
        }
      }
    }

    /// <summary>
    /// Injects the unit dictionary to be used in parameter setting.
    /// </summary>
    /// <param name="dict"></param>
    public void InjectUnitDictionaryInKits(Dictionary<string, string> dict)
    {
      var assemblies = SpeckleCore.SpeckleInitializer.GetAssemblies();
      foreach (var ass in assemblies)
      {
        var types = ass.GetTypes();
        foreach (var type in types)
        {
          if (type.GetInterfaces().Contains(typeof(SpeckleCore.ISpeckleInitializer)))
          {
            if (type.GetProperties().Select(p => p.Name).Contains("UnitDictionary"))
            {
              type.GetProperty("UnitDictionary").SetValue(null, dict);
            }
          }
        }
      }
    }

    /// <summary>
    /// Gets from a speckle kit a "MissingFamiliesAndTypes" string hashset. This is populated during conversion to revit by the ToNative methods. This list is then sent to the UI.
    /// </summary>
    /// <returns></returns>
    public List<string> GetAndClearMissingFamilies()
    {
      var assemblies = SpeckleCore.SpeckleInitializer.GetAssemblies();
      foreach (var ass in assemblies)
      {
        var types = ass.GetTypes();
        foreach (var type in types)
        {
          if (type.GetInterfaces().Contains(typeof(SpeckleCore.ISpeckleInitializer)))
          {
            var typessss = type.GetProperties().Select(p => p.Name).ToList();
            if (type.GetProperties().Select(p => p.Name).Contains("MissingFamiliesAndTypes"))
            {
              var missingSet = type.GetProperty("MissingFamiliesAndTypes").GetValue(type);
              var missingList = ((HashSet<string>)missingSet).ToList();

              type.GetProperty("MissingFamiliesAndTypes").SetValue(null, new HashSet<string>());

              return missingList;
            }
          }
        }
      }
      return null;
    }

    public object GetAndClearUnitDictionary()
    {
      var assemblies = SpeckleCore.SpeckleInitializer.GetAssemblies();

      foreach (var ass in assemblies)
      {
        var types = ass.GetTypes();
        foreach (var type in types)
        {
          if (type.GetInterfaces().Contains(typeof(SpeckleCore.ISpeckleInitializer)))
          {
            var typessss = type.GetProperties().Select(p => p.Name).ToList();
            if (type.GetProperties().Select(p => p.Name).Contains("UnitDictionary"))
            {
              var copy = new Dictionary<string, string>((Dictionary<string, string>)type.GetProperty("UnitDictionary").GetValue(type));
              type.GetProperty("UnitDictionary").SetValue(null, new Dictionary<string, string>());

              return copy;
            }
          }
        }
      }
      return null;
    }

    public void ExecuteAction(Action a)
    {
      Queue.Add(a);
      Executor.Raise();
    }

    #endregion

    #region app events
    private void RevitApp_ViewActivated(object sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
    {
      if (GetDocHash(e.Document) != GetDocHash(e.PreviousActiveView.Document))
      {
        DispatchStoreActionUi("flushClients");
        DispatchStoreActionUi("getExistingClients");

        Queue.Add(new Action(() =>
      {
        using (Transaction t = new Transaction(CurrentDoc.Document, "Switching Local Speckle State"))
        {
          t.Start();
          LocalState = SpeckleStateManager.ReadState(CurrentDoc.Document);
          InjectStateInKits();
          t.Commit();
        }
      }));
        Executor.Raise();
      }
    }

    private void Application_DocumentClosed(object sender, Autodesk.Revit.DB.Events.DocumentClosedEventArgs e)
    {
      DispatchStoreActionUi("flushClients");
    }

    private void Application_DocumentOpened(object sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
    {
      DispatchStoreActionUi("flushClients");
      DispatchStoreActionUi("getExistingClients");

      Queue.Add(new Action(() =>
    {
      using (Transaction t = new Transaction(CurrentDoc.Document, "Reading Local Speckle State"))
      {
        t.Start();
        LocalState = SpeckleStateManager.ReadState(CurrentDoc.Document);
        InjectStateInKits();
        t.Commit();
      }
    }));
      Executor.Raise();
    }

    // TODO: Handler for detecting changes in the sender
    // TODO: Mark received objects as modified in local state (x)
    private void Application_DocumentChanged(object sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
    {
      var transactionNames = e.GetTransactionNames();
      var foundKnownTransaction = transactionNames.FirstOrDefault(str => str.Contains("Speckle"));
      if (foundKnownTransaction != null) return;
      //if ( transactionNames.Contains( "Speckle Bake" ) || transactionNames.Contains( "Speckle Delete" ) ) return;

      // TODO: Notify ui that application state now differs from stream state.
      // Will require a iterating by stream rather than grouped "allStateObjects"
      var modified = e.GetModifiedElementIds();
      var modifiedUniqueIds = modified.Select(id => CurrentDoc.Document.GetElement(id).UniqueId);
      var allStateObjects = (from p in LocalState.SelectMany(s => s.Objects) select p).ToList();
      var changed = false;

      var affectedStreams = new HashSet<string>();
      var affectedClients = new List<string>();
      foreach (var stream in LocalState)
      {
        foreach (var id in modifiedUniqueIds)
        {
          var found = stream.Objects.FirstOrDefault(o => (String)o.Properties["revitUniqueId"] == id);
          if (found != null)
          {
            found.Properties["userModified"] = true;
            changed = true;
            if (affectedStreams.Add(stream.StreamId))
            {
              var client = ClientListWrapper.clients.FirstOrDefault(cl => (string)cl.streamId == stream.StreamId);
              if (client != null)
              {
                NotifyUi("update-client", JsonConvert.SerializeObject(new
                {
                  _id = client._id,
                  expired = true,
                  message = ""
                }));
              }
            }
          }
        }
      }

      if (!changed) return;

      NotifyUi("appstate-expired", JsonConvert.SerializeObject(new
      {
        affectedStreams
      }));

      Queue.Add(new Action(() =>
    {
      using (Transaction t = new Transaction(CurrentDoc.Document, "Writing Local Speckle State"))
      {
        t.Start();
        SpeckleStateManager.WriteState(CurrentDoc.Document, LocalState);
        t.Commit();
      }
    }));
      Executor.Raise();

    }

    #endregion

    #region client add/remove + serialisation/deserialisation


    /// <summary>
    /// Adds a client receiver. Does not "bake" it.
    /// </summary>
    /// <param name="args"></param>
    public override void AddReceiver(string args)
    {
      var client = JsonConvert.DeserializeObject<dynamic>(args);
      ClientListWrapper.clients.Add(client);

      Queue.Add(new Action(() =>
    {
      using (Transaction t = new Transaction(CurrentDoc.Document, "Adding Speckle Receiver"))
      {
        t.Start();
        SpeckleClientsStorageManager.WriteClients(CurrentDoc.Document, ClientListWrapper);
        t.Commit();
      }
    }));
      Executor.Raise();
    }

    /// <summary>
    /// Deletes a client, and persists the information to the file.
    /// </summary>
    /// <param name="args"></param>
    public override void RemoveClient(string args)
    {
      var client = JsonConvert.DeserializeObject<dynamic>(args);
      var index = ClientListWrapper.clients.FindIndex(cl => cl.clientId == client.clientId);

      if (index == -1) return;

      ClientListWrapper.clients.RemoveAt(index);

      var lsIndex = LocalState.FindIndex(x => x.StreamId == (string)client.streamId);
      if (lsIndex != -1) LocalState.RemoveAt(lsIndex);

      // persist the changes please
      Queue.Add(new Action(() =>
    {
      using (Transaction t = new Transaction(CurrentDoc.Document, "Removing Speckle Client"))
      {
        t.Start();
        SpeckleStateManager.WriteState(CurrentDoc.Document, LocalState);
        SpeckleClientsStorageManager.WriteClients(CurrentDoc.Document, ClientListWrapper);
        t.Commit();
      }
    }));
      Executor.Raise();
    }

    /// <summary>
    /// Gets the clients stored in this file, if any.
    /// </summary>
    /// <returns></returns>
    public override string GetFileClients()
    {
      var myReadClients = SpeckleClientsStorageManager.ReadClients(CurrentDoc.Document);
      if (myReadClients == null)
        myReadClients = new SpeckleClientsWrapper();

      // Set them up in the class so we're aware of them
      ClientListWrapper = myReadClients;

      return JsonConvert.SerializeObject(myReadClients.clients);
    }

    #endregion

    #region Client Actions

    public override void AddObjectsToSender(string args)
    {
      // NOTE: implemented in the Receiver partial class extension file. Kept here just to prevent compiler errors.
    }

    public override void RemoveObjectsFromSender(string args)
    {
      // NOTE: implemented in the Receiver partial class extension file. Kept here just to prevent compiler errors.
    }

    /// <summary>
    /// Selects the objects from a specific client (receiver or sender).
    /// </summary>
    /// <param name="args"></param>
    public override void SelectClientObjects(string args)
    {
      var client = JsonConvert.DeserializeObject<dynamic>(args);
      var objIds = LocalState.Find(stream => stream.StreamId == (string)client.streamId).Objects.Select(obj => (string)obj.Properties["revitUniqueId"]).ToList();

      Queue.Add(new Action(() =>
    {
      using (Transaction t = new Transaction(CurrentDoc.Document, "Speckle Select"))
      {
        t.Start();
        if (objIds != null)
        {
          var objElIds = objIds.Select(obj => CurrentDoc.Document.GetElement(obj)?.Id).Where(id => id != null).ToList();
          using (var myUiDoc = new UIDocument(CurrentDoc.Document))
          {
            myUiDoc.Selection.SetElementIds(objElIds);
            myUiDoc.ShowElements(objElIds);
          }
        }
        t.Commit();
      }
    }));
      Executor.Raise();
    }

    #endregion

    #region document info
    public override string GetApplicationHostName()
    {
      return "Revit";
    }

    public override string GetFileName()
    {
      return CurrentDoc.Document.Title;
    }

    public override string GetDocumentId()
    {
      return GetDocHash(CurrentDoc.Document);
      // NOTE: If project is copy pasted, it has the same unique id, so the below is not reliable
      //return CurrentDoc.Document.ProjectInformation.UniqueId;
    }

    private string GetDocHash(Document doc)
    {
      return SpeckleCore.Converter.getMd5Hash(doc.PathName + doc.Title);
    }

    public override string GetDocumentLocation()
    {
      return CurrentDoc.Document.PathName;
    }
    #endregion

    #region sender 

    //public string GetObjectSelection()
    //{
    //  List<dynamic> selectedObjects = new List<dynamic>();

    //  if (CurrentDoc == null) return JsonConvert.SerializeObject(selectedObjects); ;

    //  var selectionIds = CurrentDoc.Selection.GetElementIds();
    //  foreach (var id in selectionIds)
    //  {
    //    var elm = CurrentDoc.Document.GetElement(id);
    //    var cat = elm.Category;
    //    var typ = elm.GetType();
    //    var isFam = elm is FamilyInstance;

    //    if (isFam)
    //    {
    //      var fam = (elm as FamilyInstance).Symbol.FamilyName;
    //    }

    //    selectedObjects.Add(new
    //    {
    //      id = elm.UniqueId.ToString(),
    //      type = typ.Name,
    //      cat = cat.Name
    //    });
    //  }

    //  return JsonConvert.SerializeObject(selectedObjects);
    //}

    public override List<ISelectionFilter> GetSelectionFilters()
    {
      var categories = new List<string>();
      var parameters = new List<string>();
      if (CurrentDoc != null)
      {
        //selectionCount = CurrentDoc.Selection.GetElementIds().Count();
        categories = Globals.GetCategoryNames(CurrentDoc.Document);
        parameters = Globals.GetParameterNames(CurrentDoc.Document);
      }


      return new List<ISelectionFilter>
      {
        new ElementsSelectionFilter
        {
          Name = "Selection",
          Icon = "mouse",
          Selection = new List<string>()
        },
        new ListSelectionFilter
        {
          Name = "Category",
          Icon = "category",
          Values = categories
        },
        new PropertySelectionFilter
        {
          Name = "Parameter",
          Icon = "filter_list",
          HasCustomProperty = false,
          Values = parameters,
          Operators = new List<string>
          {
            "equals",
            "contains",
            "is greater than",
            "is less than"
          }
        }
      };
    }

    private static List<T> IEnumeratorToList<T>(System.Collections.IEnumerator enumerator)
    {
      List<T> list = new List<T>();
      while (enumerator.MoveNext())
      {
        list.Add((T)Convert.ChangeType(enumerator.Current, typeof(T)));
      }
      return list;
    }



    #endregion
  }

}
