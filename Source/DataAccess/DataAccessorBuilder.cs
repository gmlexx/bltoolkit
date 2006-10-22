using System;
using System.IO;
using System.Reflection;
using System.Data;
using System.Data.SqlTypes;
using System.Collections;
using System.Reflection.Emit;
using System.Xml;

using BLToolkit.Common;
using BLToolkit.TypeBuilder.Builders;
using BLToolkit.Data;
using BLToolkit.Reflection.Emit;
using BLToolkit.Reflection;
using BLToolkit.TypeBuilder;
using BLToolkit.Mapping;

namespace BLToolkit.DataAccess
{
	public class DataAccessorBuilder : AbstractTypeBuilderBase
	{
		private struct MapOutputParametersValue
		{
			public string           ReturnValueMember;
			public ParameterInfo    ParameterInfo;

			public MapOutputParametersValue(string returnValueMember, ParameterInfo parameterInfo)
			{
				ReturnValueMember = returnValueMember;
				ParameterInfo     = parameterInfo;
			}
		}

		public override int GetPriority(BuildContext context)
		{
			return TypeBuilderConsts.Priority.DataAccessor;
		}

		public override bool IsApplied(BuildContext context, AbstractTypeBuilderList builders)
		{
			return context.IsBuildStep && context.IsAbstractMethod;
		}

		const BindingFlags _bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

		Type            _baseType = typeof(DataAccessor);
		Type            _objectType;
		bool            _explicitObjectType;
		ParameterInfo[] _parameters;
		ArrayList       _paramList;
		ArrayList       _refParamList;
		bool            _createManager;
		LocalBuilder    _locManager;
		LocalBuilder    _locObjType;
		ArrayList       _outputParameters;
		string          _sqlText;
		ArrayList       _formatParamList;
		ParameterInfo   _destination;
		ArrayList       _mapOutputParameters;

		protected override void BuildAbstractMethod()
		{
			// Any class variable must be initialized before use
			// as the same instance of the class is utilized to build abstract methods.
			//
			_paramList           = new ArrayList();
			_refParamList        = new ArrayList();
			_formatParamList     = new ArrayList();
			_mapOutputParameters = new ArrayList();
			_destination         = null;
			_createManager       = true;
			_objectType          = null;
			_explicitObjectType  = false;
			_parameters          = Context.CurrentMethod.GetParameters();
			_locManager          = Context.MethodBuilder.Emitter.DeclareLocal(typeof(DbManager));
			_locObjType          = Context.MethodBuilder.Emitter.DeclareLocal(typeof(Type));
			_outputParameters    = null;
			_sqlText             = null;

			GetSqlQueryCommand();
			ProcessParameters();
			CreateDbManager();
			SetObjectType();

			// Define execute method type.
			//
			Type returnType = _destination != null? _destination.ParameterType: Context.CurrentMethod.ReturnType;

			if (returnType == typeof(IDataReader))
			{
				ExecuteReader();
			}
			else if (returnType == typeof(DataSet) || returnType.IsSubclassOf(typeof(DataSet)))
			{
				ExecuteDataSet(returnType);
			}
			else if (returnType == typeof(DataTable) || returnType.IsSubclassOf(typeof(DataTable)))
			{
				ExecuteDataTable();
			}
			else if (!returnType.IsArray && (IsInterfaceOf(returnType, typeof(IList))
#if FW2
				|| returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IList<>)
#endif
				))
			{
				if (!_explicitObjectType)
				{
					Type elementType = TypeHelper.GetListItemType(returnType);

					if (elementType == typeof(object) && _destination != null)
						elementType = TypeHelper.GetListItemType(Context.CurrentMethod.ReturnType);

					if (elementType != typeof(object))
						_objectType = elementType;
				}

				if (_objectType == null || _objectType == typeof(object))
					throw new TypeBuilderException(string.Format(
						"Can not determine object type for method '{0}.{1}'",
						Context.CurrentMethod.DeclaringType.Name,
						Context.CurrentMethod.Name));

				if (TypeHelper.IsScalar(_objectType))
					ExecuteScalarList();
				else
					ExecuteList();
			}
			else if (IsInterfaceOf(returnType, typeof(IDictionary))
#if FW2
				|| returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IDictionary<,>)
#endif
				)
			{
				Type elementType = null;
				Type keyType     = typeof(object);
#if FW2
				Type[] gTypes = TypeHelper.GetGenericArguments(returnType, typeof(IDictionary));

				if ((gTypes == null || gTypes.Length != 2) && _destination != null)
					gTypes = TypeHelper.GetGenericArguments(_destination.ParameterType, typeof(IDictionary));

				if (gTypes != null && gTypes.Length == 2)
				{
					keyType     = gTypes[0];
					elementType = gTypes[1];
				}
#endif

				if (elementType == null || _explicitObjectType)
					elementType = _objectType;

				if (elementType == null || elementType == typeof(object))
					throw new TypeBuilderException(string.Format(
						"Can not determine object type for the method '{0}.{1}'",
						Context.CurrentMethod.DeclaringType.Name,
						Context.CurrentMethod.Name));

				bool isIndex = TypeHelper.IsSameOrParent(typeof(CompoundValue), keyType);

				if (keyType != typeof(object) && !isIndex && !TypeHelper.IsScalar(keyType))
					throw new TypeBuilderException(string.Format(
						"Key type for the method '{0}.{1}' can be of type object, CompoundValue, or a scalar type.",
						Context.CurrentMethod.DeclaringType.Name,
						Context.CurrentMethod.Name));

				MethodInfo mi = Context.CurrentMethod;

				object[] attrs = mi.GetCustomAttributes(typeof(IndexAttribute), true);
				NameOrIndexParameter[] fields = new NameOrIndexParameter[0];

				if (attrs.Length != 0)
					fields = ((IndexAttribute)attrs[0]).Fields;

				if (fields.Length > 1 && keyType != typeof(object) && !isIndex)
					throw new TypeBuilderException(string.Format(
						"Key type for the method '{0}.{1}' can be of type object or CompoundValue.",
						Context.CurrentMethod.DeclaringType.Name,
						Context.CurrentMethod.Name));

				if (TypeHelper.IsScalar(elementType))
				{
					attrs = mi.GetCustomAttributes(typeof(ScalarFieldNameAttribute), true);

					if (attrs.Length == 0)
						throw new TypeBuilderException(string.Format(
							"Scalar field name is not defined for the method '{0}.{1}'.",
							Context.CurrentMethod.DeclaringType.Name,
							Context.CurrentMethod.Name));

					NameOrIndexParameter scalarField = ((ScalarFieldNameAttribute)attrs[0]).NameOrIndex;

					if (fields.Length == 0)
						ExecuteScalarDictionaryWithPK(keyType, scalarField, elementType);
					else if (isIndex || fields.Length > 1)
						ExecuteScalarDictionaryWithMapIndex(fields, scalarField, elementType);
					else
						ExecuteScalarDictionaryWithScalarKey(fields[0], keyType, scalarField, elementType);
				}
				else
				{
					if (fields.Length == 0)
						ExecuteDictionaryWithPK(keyType, elementType);
					else if (isIndex || fields.Length > 1)
						ExecuteDictionaryWithMapIndex(fields, elementType);
					else
						ExecuteDictionaryWithScalarKey(fields[0], elementType);
				}
			}
			else if (returnType == typeof(void))
			{
				ExecuteNonQuery();
			}
			else if (TypeHelper.IsScalar(returnType))
			{
				ExecuteScalar();
			}
			else
			{
				if (_objectType == null)
					_objectType = returnType;

				ExecuteObject();
			}

			GetOutRefParameters();
			Finally();
		}

		private void GetSqlQueryCommand()
		{
			object[] attrs = Context.CurrentMethod.GetCustomAttributes(typeof(SqlQueryAttribute), true);

			if (attrs.Length != 0)
			{
				_sqlText = ((SqlQueryAttribute)attrs[0]).SqlText;

				if (_sqlText != null && _sqlText.Length == 0)
					_sqlText = null;
			}
		}

		private void AddParameter(ParameterInfo pi)
		{
			Type pType = pi.ParameterType;

			if (pType.IsByRef)
				pType = pType.GetElementType();

			if (TypeHelper.IsScalar(pType))
				_paramList.Add(pi);
			else if (pType == typeof(DbManager) || pType.IsSubclassOf(typeof(DbManager)))
				_createManager = false;
			else
				_refParamList.Add(pi);
		}

		private void ProcessParameters()
		{
			for (int i = 0; i < _parameters.Length; i++)
			{
				ParameterInfo       pi = _parameters[i];
				NoMapAttribute[] attrs = (NoMapAttribute[])pi.GetCustomAttributes(typeof(NoMapAttribute), true);

				if (attrs.Length == 0)
					AddParameter(pi);
				else
				{
					for(int j = 0; j < attrs.Length; ++j)
					{
						if (!attrs[j].NoMap)
							AddParameter(pi);

						if (attrs[j] is FormatAttribute)
						{
							int index = ((FormatAttribute)attrs[j]).Index;

							if (index < 0)
								index = 0;
							else if (index > _formatParamList.Count)
								index = _formatParamList.Count;

							_formatParamList.Insert(index, pi);
						}
						else if (attrs[j] is DestinationAttribute)
						{
							if (_destination != null)
								throw new TypeBuilderException("More then one parameter is marked as destination");

							_destination = pi;
						}
					}
				}
			}
		}

		private void CreateDbManager()
		{
			EmitHelper emit = Context.MethodBuilder.Emitter;

			if (_createManager)
			{
				emit
					.ldarg_0
					.callvirt              (typeof(DataAccessor), "GetDbManager")
					.stloc                 (_locManager)
					.BeginExceptionBlock()
					;
			}
			else
			{
				for (int i = 0; i < _parameters.Length; i++)
				{
					Type pType = _parameters[i].ParameterType;

					if (pType == typeof(DbManager) || pType.IsSubclassOf(typeof(DbManager)))
					{
						emit
							.ldarg   (_parameters[i])
							.stloc   (_locManager)
							;

						break;
					}
				}
			}
		}

		private void SetObjectType()
		{
			MethodInfo mi = Context.CurrentMethod;

			object[] attrs = mi.GetCustomAttributes(typeof(ObjectTypeAttribute), true);

			if (attrs.Length == 0)
				attrs = mi.DeclaringType.GetCustomAttributes(typeof(ObjectTypeAttribute), true);
			else
				_explicitObjectType = true;

			if (attrs.Length != 0)
				_objectType = ((ObjectTypeAttribute)attrs[0]).ObjectType;

#if FW2
			if (_objectType == null)
			{
				Type[] types = TypeHelper.GetGenericArguments(mi.DeclaringType, typeof(DataAccessor));

				if (types != null)
					_objectType = types[0];
			}
#endif
		}

		#region ExecuteReader

		private void ExecuteReader()
		{
			InitObjectType();
			GetSprocName();
			CallSetCommand();

			object[] attrs = Context.CurrentMethod.GetCustomAttributes(typeof(CommandBehaviorAttribute), true);

			if (attrs.Length == 0)
			{
				Context.MethodBuilder.Emitter
					.callvirt (typeof(DbManager).GetMethod("ExecuteReader", Type.EmptyTypes))
					.stloc    (Context.ReturnValue)
					;
			}
			else
			{
				Context.MethodBuilder.Emitter
					.ldc_i4_  ((int)((CommandBehaviorAttribute)attrs[0]).CommandBehavior)
					.callvirt (typeof(DbManager), "ExecuteReader", typeof(CommandBehavior))
					.stloc    (Context.ReturnValue)
					;
			}
		}

		#endregion

		#region ExecuteDataSet

		private void ExecuteDataSet(Type returnType)
		{
			CreateReturnTypeInstance();
			InitObjectType();
			GetSprocName();
			CallSetCommand();

			EmitHelper emit = Context.MethodBuilder.Emitter;

			if (returnType == typeof(DataSet))
			{
				LoadDestinationOrReturnValue();

				emit
					.callvirt  (typeof(DbManager), "ExecuteDataSet", typeof(DataSet))
					.pop
					.end()
					;
			}
			else
			{
				emit
					.pop
					.end()
					;

				LoadDestinationOrReturnValue();

				Label l1 = emit.DefineLabel();
				Label l2 = emit.DefineLabel();

				emit
					.callvirt  (typeof(DataSet).GetProperty("Tables").GetGetMethod())
					.callvirt  (typeof(InternalDataCollectionBase).GetProperty("Count").GetGetMethod())
					.ldc_i4_0
					.ble_s(l1)
					.ldloc     (_locManager);

				LoadDestinationOrReturnValue();
				LoadDestinationOrReturnValue();

				emit
					.callvirt  (typeof(DataSet).GetProperty("Tables").GetGetMethod())
					.ldc_i4_0
					.callvirt  (typeof(DataTableCollection), "get_Item", typeof(int))
					.callvirt  (typeof(DataTable).GetProperty("TableName").GetGetMethod())
					.call      (typeof(NameOrIndexParameter), "op_Implicit", typeof(string))
					.callvirt  (typeof(DbManager), "ExecuteDataSet", typeof(DataSet), typeof(NameOrIndexParameter))
					.pop
					.br_s      (l2)
					.MarkLabel (l1)
					.ldloc     (_locManager);

				LoadDestinationOrReturnValue();

				emit
					.callvirt  (typeof(DbManager), "ExecuteDataSet", typeof(DataSet))
					.pop
					.MarkLabel (l2)
					;
			}
		}

		#endregion

		#region ExecuteDataTable

		private void ExecuteDataTable()
		{
			CreateReturnTypeInstance();
			InitObjectType();
			GetSprocName();
			CallSetCommand();
			LoadDestinationOrReturnValue();

			Context.MethodBuilder.Emitter
				.callvirt (typeof(DbManager), "ExecuteDataTable", typeof(DataTable))
				.pop
				.end()
				;
		}

		#endregion

		#region ExecuteScalarList

		private void ExecuteScalarList()
		{
			CreateReturnTypeInstance();
			InitObjectType();
			GetSprocName();
			CallSetCommand();
			LoadDestinationOrReturnValue();

			object[] attrs = Context.CurrentMethod.GetCustomAttributes(typeof(ScalarFieldNameAttribute), true);

			if (attrs.Length == 0)
			{
				Context.MethodBuilder.Emitter
					.ldloc(_locObjType)
					.callvirt(typeof(DbManager), "ExecuteScalarList", typeof(IList), typeof(Type))
					.pop
					.end()
					;
			}
			else
			{
				Context.MethodBuilder.Emitter
					.ldloc(_locObjType)
					.ldNameOrIndex(((ScalarFieldNameAttribute)attrs[0]).NameOrIndex)
					.callvirt(typeof(DbManager), "ExecuteScalarList", typeof(IList), typeof(Type), typeof(NameOrIndexParameter))
					.pop
					.end()
					;
			}
		}

		private void ExecuteList()
		{
			CreateReturnTypeInstance();
			InitObjectType();
			GetSprocName();
			CallSetCommand();
			LoadDestinationOrReturnValue();

			Context.MethodBuilder.Emitter
				.ldloc    (_locObjType)
				.callvirt (typeof(DbManager), "ExecuteList", typeof(IList), typeof(Type))
				.pop
				.end()
				;
		}

		#endregion

		#region ExecuteDictionary

		public FieldBuilder GetIndexField(NameOrIndexParameter[] namesOrIndexes)
		{
#if FW2
			string id = "index$" + string.Join("%",
				Array.ConvertAll<NameOrIndexParameter, string>(namesOrIndexes,
					delegate(NameOrIndexParameter nameOrIndex)
					{
						return nameOrIndex.ToString();
					}));
#else
			System.Text.StringBuilder sb = new System.Text.StringBuilder();
			sb.Append("index$");

			for (int i = 0; i < namesOrIndexes.Length; ++i)
			{
				sb.Append('%');
				sb.Append(namesOrIndexes[i]);
			}

			string id = sb.ToString();
#endif

			FieldBuilder fieldBuilder = Context.GetField(id);

			if (fieldBuilder == null)
			{
				fieldBuilder = Context.CreatePrivateStaticField(id, typeof(MapIndex));

				EmitHelper   emit = Context.TypeBuilder.TypeInitializer.Emitter;
				LocalBuilder arr  = emit.DeclareLocal(typeof(NameOrIndexParameter[]));

				emit
					.ldc_i4_ (namesOrIndexes.Length)
					.newarr  (typeof(NameOrIndexParameter))
					.stloc   (arr)
					;

				for (int i = 0; i < namesOrIndexes.Length; i++)
				{
					emit
						.ldloc(arr)
						.ldc_i4_(i)
						.ldelema(typeof(NameOrIndexParameter));

					if (namesOrIndexes[i].ByName)
					{
						emit.ldstr(namesOrIndexes[i].Name)
							.call(typeof(NameOrIndexParameter), "op_Implicit", typeof(string));
					}
					else
					{
						emit.ldc_i4_(namesOrIndexes[i].Index)
							.call(typeof(NameOrIndexParameter), "op_Implicit", typeof(int));
					}
					emit
						.stobj(typeof(NameOrIndexParameter))
						.end()
						;
				}
				
				emit
					.ldloc  (arr)
					.newobj (typeof(MapIndex), typeof(NameOrIndexParameter[]))
					.stsfld (fieldBuilder)
					;
			}

			return fieldBuilder;
		}

		/// <summary>
		/// Maps primary keys(s) to a scalar field.
		/// </summary>
		private void ExecuteScalarDictionaryWithPK(
			Type                 keyType,
			NameOrIndexParameter scalarField,
			Type                 elementType)
		{
			CreateReturnTypeInstance();
			InitObjectType();

			Context.MethodBuilder.Emitter
				.ldarg_0
				.end()
				;

			GetSprocName();
			CallSetCommand();
			LoadDestinationOrReturnValue();

			Context.MethodBuilder.Emitter
				.ldloc         (_locObjType)
				.LoadType      (keyType)
				.ldstr         (Context.CurrentMethod.Name)
				.ldNameOrIndex (scalarField)
				.LoadType      (elementType)
				.callvirt      (typeof(DataAccessor), "ExecuteScalarDictionary", _bindingFlags,
					            typeof(DbManager), typeof(IDictionary), typeof(Type),
					            typeof(Type), typeof(string), typeof(NameOrIndexParameter), typeof(Type))
				;
		}

		/// <summary>
		/// Maps a complex index to a scalar field.
		/// </summary>
		private void ExecuteScalarDictionaryWithMapIndex(
			NameOrIndexParameter[] index,
			NameOrIndexParameter   scalarField,
			Type                   elementType)
		{
			_objectType = elementType;

			CreateReturnTypeInstance();
			InitObjectType();
			GetSprocName();
			CallSetCommand();
			LoadDestinationOrReturnValue();

			Context.MethodBuilder.Emitter
				.ldsfld        (GetIndexField(index))
				.ldNameOrIndex (scalarField)
				.ldloc         (_locObjType)
				.callvirt      (typeof(DbManager), "ExecuteScalarDictionary",
					            typeof(IDictionary), typeof(MapIndex),
					            typeof(NameOrIndexParameter), typeof(Type))
				.pop
				.end()
				;
		}

		/// <summary>
		/// Maps any single field to any (other) single field.
		/// </summary>
		private void ExecuteScalarDictionaryWithScalarKey(
			NameOrIndexParameter keyField,    Type keyType,
			NameOrIndexParameter scalarField, Type elementType)
		{
			_objectType = elementType;

			CreateReturnTypeInstance();
			InitObjectType();
			GetSprocName();
			CallSetCommand();
			LoadDestinationOrReturnValue();

			Context.MethodBuilder.Emitter
				.ldNameOrIndex (keyField)
				.LoadType      (keyType)
				.ldNameOrIndex (scalarField)
				.ldloc         (_locObjType)
				.callvirt      (typeof(DbManager), "ExecuteScalarDictionary",
					            typeof(IDictionary), typeof(NameOrIndexParameter), typeof(Type),
					            typeof(NameOrIndexParameter), typeof(Type))
				.pop
				.end()
				;
		}

		/// <summary>
		/// Maps primary keys(s) to an object of the specified type.
		/// </summary>
		private void ExecuteDictionaryWithPK(
			Type keyType,
			Type elementType)
		{
			_objectType = elementType;

			CreateReturnTypeInstance();
			InitObjectType();

			Context.MethodBuilder.Emitter
				.ldarg_0
				.end()
				;

			GetSprocName();
			CallSetCommand();
			LoadDestinationOrReturnValue();

			Context.MethodBuilder.Emitter
				.ldloc    (_locObjType)
				.LoadType (keyType)
				.ldstr    (Context.CurrentMethod.Name)
				.callvirt (typeof(DataAccessor), "ExecuteDictionary", _bindingFlags,
					       typeof(DbManager), typeof(IDictionary), typeof(Type),
					       typeof(Type), typeof(string))
				;
		}

		/// <summary>
		/// Maps a complex index to an object of the specified type.
		/// </summary>
		private void ExecuteDictionaryWithMapIndex(
			NameOrIndexParameter[] index,
			Type                   elementType)
		{
			_objectType = elementType;

			CreateReturnTypeInstance();
			InitObjectType();
			GetSprocName();
			CallSetCommand();
			LoadDestinationOrReturnValue();

			Context.MethodBuilder.Emitter
				.ldsfld   (GetIndexField(index))
				.ldloc    (_locObjType)
				.ldnull
				.callvirt (typeof(DbManager), "ExecuteDictionary",
					       typeof(IDictionary), typeof(MapIndex), typeof(Type), typeof(object[]))
				.pop
				.end()
				;
		}

		/// <summary>
		/// Maps any single field to object type.
		/// </summary>
		private void ExecuteDictionaryWithScalarKey(
			NameOrIndexParameter keyField,
			Type                 elementType)
		{
			_objectType = elementType;

			CreateReturnTypeInstance();
			InitObjectType();
			GetSprocName();
			CallSetCommand();
			LoadDestinationOrReturnValue();

			Context.MethodBuilder.Emitter
				.ldNameOrIndex(keyField)
				.ldloc        (_locObjType)
				.ldnull
				.callvirt     (typeof(DbManager), "ExecuteDictionary",
					           typeof(IDictionary), typeof(NameOrIndexParameter), typeof(Type), typeof(object[]))
				.pop
				.end()
				;
		}

		#endregion

		#region ExecuteNonQuery

		public void ExecuteNonQuery()
		{
			if (_destination != null)
				throw new TypeBuilderException("ExecuteNonQuery does not support the Destination attribute");

			InitObjectType();
			GetSprocName();
			CallSetCommand();

			MethodInfo   mi      = typeof(DbManager).GetMethod("ExecuteNonQuery", Type.EmptyTypes);
			LocalBuilder locExec = Context.MethodBuilder.Emitter.DeclareLocal(mi.ReturnType);

			Context.MethodBuilder.Emitter
				.callvirt (mi)
				.stloc    (locExec)
				;

			if (Context.ReturnValue != null)
			{
				Context.MethodBuilder.Emitter
					.ldloc (locExec)
					.stloc (Context.ReturnValue)
					;
			}
		}

		#endregion

		#region ExecuteScalar

		public void ExecuteScalar()
		{
			if (_destination != null)
				throw new TypeBuilderException("ExecuteScalar does not support the Destination attribute");

			Context.MethodBuilder.Emitter
				.ldarg_0
				.ldloc   (_locManager)
				;

			InitObjectType();
			GetSprocName();
			CallSetCommand();

			EmitHelper emit = Context.MethodBuilder.Emitter;
			object[] attrs  = Context.CurrentMethod.GetCustomAttributes(typeof(ScalarSourceAttribute), true);

			if (attrs.Length == 0)
			{
				emit.callvirtNoGenerics(typeof(DbManager), "ExecuteScalar");
			}
			else
			{
				ScalarSourceAttribute attr = (ScalarSourceAttribute)attrs[0];

				emit
					.ldc_i4_((int)attr.ScalarType)
					.ldNameOrIndex(attr.NameOrIndex)
					.callvirtNoGenerics(typeof(DbManager), "ExecuteScalar", typeof(ScalarSourceType), typeof(NameOrIndexParameter));
			}

			string converterName = GetConverterMethodName(Context.CurrentMethod.ReturnType);

			if (converterName == null)
			{
				emit
					.LoadType(Context.CurrentMethod.ReturnType)
					.ldnull
					.callvirt(typeof (DataAccessor), "ConvertChangeType", _bindingFlags, typeof (DbManager), typeof (object), typeof (Type), typeof (object))
					.stloc(Context.ReturnValue)
					;
				
			}
			else
			{
				emit
					.ldnull
					.callvirt(typeof(DataAccessor), converterName, _bindingFlags, typeof(DbManager), typeof(object), typeof(object))
					.stloc(Context.ReturnValue)
					;
			}
		}

		#endregion

		#region ExecuteObject

		public void ExecuteObject()
		{
			InitObjectType();
			GetSprocName();
			CallSetCommand();

			EmitHelper emit = Context.MethodBuilder.Emitter;

			if (_destination != null)
			{
				emit
					.ldarg(_destination)
					.callvirt(typeof(DbManager), "ExecuteObject", typeof(Object))
					;
			}
			else
			{
				emit
					.ldloc(_locObjType)
					.callvirt(typeof(DbManager), "ExecuteObject", typeof(Type))
					;
			}

			if (null != Context.ReturnValue)
			{
				emit
					.castclass(_objectType)
					.stloc(Context.ReturnValue)
					;
			}
			else
			{
				emit
					.pop
					.end()
					;
			}
		}

		#endregion

		private void Finally()
		{
			if (_createManager)
			{
				Label        fin = Context.MethodBuilder.Emitter.DefineLabel();
				PropertyInfo pi  = typeof(DataAccessor)
					.GetProperty("DisposeDbManager", BindingFlags.NonPublic | BindingFlags.Instance);

				Context.MethodBuilder.Emitter
					.BeginFinallyBlock()
					.ldloc               (_locManager)
					.brfalse_s           (fin)
					.ldarg_0
					.callvirt            (pi.GetGetMethod(true))
					.brfalse_s           (fin)
					.ldloc               (_locManager)
					.callvirt            (typeof(IDisposable), "Dispose")
					.MarkLabel           (fin)
					.EndExceptionBlock()
					;
			}
		}

		private void CreateReturnTypeInstance()
		{
			if (null == Context.ReturnValue)
				return;

			if (null != _destination)
			{
				Context.MethodBuilder.Emitter
					.ldarg    (_destination)
					.stloc    (Context.ReturnValue)
					;
			}
			else
			{
				ConstructorInfo ci = TypeHelper.GetDefaultConstructor(Context.CurrentMethod.ReturnType);

				if (ci == null)
					throw new TypeBuilderException(string.Format("Cannot create an instance of the type '{0}'",
						Context.CurrentMethod.ReturnType.FullName));

				Context.MethodBuilder.Emitter
					.newobj   (ci)
					.stloc    (Context.ReturnValue)
					;
			}
		}

		private void LoadDestinationOrReturnValue()
		{
			if (_destination != null)
				Context.MethodBuilder.Emitter.ldarg(_destination);
			else
				Context.MethodBuilder.Emitter.ldloc(Context.ReturnValue);
		}

		private void InitObjectType()
		{
			if (_objectType == null)
			{
				Context.MethodBuilder.Emitter
					.ldnull
					.stloc    (_locObjType)
					;
			}
			else
			{
				Context.MethodBuilder.Emitter
					.LoadType (_objectType)
					.stloc    (_locObjType)
					;
			}
		}

		private void GetSprocName()
		{
			EmitHelper emit = Context.MethodBuilder.Emitter;

			if (_sqlText != null)
			{
				emit
					.ldloc    (_locManager)
					.ldstr    (_sqlText)
					;
			}
			else
			{
				object[] attrs = Context.CurrentMethod.GetCustomAttributes(typeof(SprocNameAttribute), true);

				if (attrs.Length == 0)
				{
					attrs = Context.CurrentMethod.GetCustomAttributes(typeof(ActionNameAttribute), true);

					string actionName = attrs.Length == 0 ?
						Context.CurrentMethod.Name : ((ActionNameAttribute)attrs[0]).Name;

					// Call GetSpName.
					//
					emit
						.ldloc    (_locManager)
						.ldarg_0
						.ldloc    (_locObjType)
						.ldstr    (actionName)
						.callvirt (_baseType, "GetSpName", _bindingFlags, typeof(Type), typeof(string))
						;
				}
				else
				{
					emit
						.ldloc    (_locManager)
						.ldstr    (((SprocNameAttribute)attrs[0]).Name)
						;
				}
			}

			// string.Format
			//
			if (_formatParamList.Count > 0)
			{
				LocalBuilder locParams = emit.DeclareLocal(typeof(object[]));

				emit
					.ldc_i4_      (_formatParamList.Count)
					.newarr       (typeof(object))
					.stloc        (locParams)
					;

				for (int i = 0; i < _formatParamList.Count; i++)
				{
					ParameterInfo pi = (ParameterInfo)_formatParamList[i];

					emit
						.ldloc          (locParams)
						.ldc_i4_        (i)
						.ldarg          (pi)
						.boxIfValueType (pi.ParameterType)
						.stelem_ref
						.end()
						;
				}

				emit
					.ldloc        (locParams)
					.call         (typeof(string), "Format", typeof(string), typeof(object[]))
					;
			}
		}

		private void CallSetCommand()
		{
			EmitHelper emit = Context.MethodBuilder.Emitter;

			// Get DiscoverParametersAttribute.
			//
			object[] attrs =
				Context.CurrentMethod.DeclaringType.GetCustomAttributes(typeof(DiscoverParametersAttribute), true);

			bool discoverParams = false;

			if (_sqlText == null)
			{
				discoverParams = attrs.Length == 0?
					false: ((DiscoverParametersAttribute)attrs[0]).Discover;

				attrs = Context.CurrentMethod.GetCustomAttributes(typeof(DiscoverParametersAttribute), true);

				if (attrs.Length != 0)
					discoverParams = ((DiscoverParametersAttribute)attrs[0]).Discover;
			}

			LocalBuilder locParams = discoverParams?
				BuildParametersWithDiscoverParameters():
				BuildParameters();

			// Call SetSpCommand.
			//
			string methodName = _sqlText == null? "SetSpCommand":   "SetCommand";
			Type   paramType  = _sqlText == null? typeof(object[]): typeof(IDbDataParameter[]);

			emit
				.ldloc    (locParams)
				.callvirt (typeof(DbManager), methodName, _bindingFlags, typeof(string), paramType)
				;
		}

		private LocalBuilder BuildParameters()
		{
			return _refParamList.Count > 0?
				BuildRefParameters():
				BuildSimpleParameters();
		}

		private LocalBuilder BuildSimpleParameters()
		{
			EmitHelper emit = Context.MethodBuilder.Emitter;

			// Parameters.
			//
			LocalBuilder locParams = emit.DeclareLocal(
				_sqlText == null? typeof(object[]): typeof(IDbDataParameter[]));

			emit
				.ldc_i4_      (_paramList.Count)
				.newarr       (_sqlText == null? typeof(object): typeof(IDbDataParameter))
				.stloc        (locParams)
				;

			for (int i = 0; i < _paramList.Count; i++)
			{
				ParameterInfo pi = (ParameterInfo)_paramList[i];

				emit
					.ldloc    (locParams)
					.ldc_i4_  (i)
					;

				BuildParameter(pi);

				emit
					.stelem_ref
					.end()
					;
			}

			return locParams;
		}

		private FieldBuilder CreateStringArrayField(object[] attrs)
		{
			if (attrs.Length == 0)
				return null;

			ArrayList list = new ArrayList();

			foreach (Direction attr in attrs)
				if (attr.Members != null)
					list.AddRange(attr.Members);

			if (list.Count == 0)
				return null;

			list.Sort(CaseInsensitiveComparer.Default);

			string[] strings = (string[]) list.ToArray(typeof(string));

			// There a no limit for a filed name length, but Visual Studio Debugger
			// may crash on fields with name longer then 256 symbols.
			//
			string       key            = "_string_array$" + string.Join("%", strings);
			FieldBuilder fieldBuilder   = Context.GetField(key);

			if (null == fieldBuilder)
			{
				fieldBuilder = Context.CreatePrivateStaticField(key, typeof(string[]));

				EmitHelper   emit       = Context.TypeBuilder.TypeInitializer.Emitter;
				LocalBuilder localArray = emit.DeclareLocal(typeof(string[]));

				emit
					.ldc_i4_           (strings.Length)
					.newarr            (typeof(string))
					.stloc             (localArray)
					;

				for (int i = 0; i < strings.Length; i++)
				{
					emit
						.ldloc         (localArray)
						.ldc_i4_       (i)
						.ldstr         (strings[i])
						.stelem_ref
						.end()
						;
				}
				
				emit
					.ldloc             (localArray)
					.stsfld            (fieldBuilder)
					;
				}

			return fieldBuilder;
		}

		private LocalBuilder BuildRefParameters()
		{
			EmitHelper   emit      = Context.MethodBuilder.Emitter;

			// Parameters.
			//
			LocalBuilder locParams = emit.DeclareLocal(typeof(object[]));

			emit
				.ldc_i4_               (_parameters.Length)
				.newarr                (typeof(object))
				.stloc                 (locParams)
				;

			for (int i = 0; i < _parameters.Length; i++)
			{
				ParameterInfo pi = _parameters[i];

				emit
					.ldloc             (locParams)
					.ldc_i4_           (i)
					;

				if (_paramList.Contains(pi))
				{
					BuildParameter(pi);
				}
				else if (_refParamList.Contains(pi))
				{
					bool         mapOutputParameters = false;
					string       returnValueMember   = null;
					FieldBuilder fieldBuilder;
					Type         type =
						pi.ParameterType == typeof(DataRow) || pi.ParameterType.IsSubclassOf(typeof(DataRow))?
							typeof(DataRow): typeof(object);

					emit
						.ldloc         (_locManager)
						.ldarg         (pi)
						;

					fieldBuilder = CreateStringArrayField(pi.GetCustomAttributes(typeof(Direction.OutputAttribute),      true));
					if (fieldBuilder != null)
					{
						emit.ldsfld    (fieldBuilder);
						mapOutputParameters = true;
					}
					else
						emit.ldnull.end();

					fieldBuilder = CreateStringArrayField(pi.GetCustomAttributes(typeof(Direction.InputOutputAttribute), true));
					if (fieldBuilder != null)
					{
						emit.ldsfld    (fieldBuilder);
						mapOutputParameters = true;
					}
					else
						emit.ldnull.end();

					fieldBuilder = CreateStringArrayField(pi.GetCustomAttributes(typeof(Direction.IgnoreAttribute),      true));
					if (fieldBuilder != null)
						emit.ldsfld    (fieldBuilder);
					else
						emit.ldnull.end();

					emit
						.ldnull
						.callvirt      (typeof(DbManager), "CreateParameters", type,
							typeof(string[]), typeof(string[]), typeof(string[]), typeof(IDbDataParameter[]))
						;

					object[] attrs = pi.GetCustomAttributes(typeof (Direction.ReturnValueAttribute), true);

					if (attrs.Length != 0)
						returnValueMember = ((Direction.ReturnValueAttribute)attrs[0]).Member;

					if (null != returnValueMember || mapOutputParameters)
						_mapOutputParameters.Add(new MapOutputParametersValue(returnValueMember, pi));
				}
				else
				{
					emit
						.ldnull
						.end()
						;
				}

				emit
					.stelem_ref
					.end()
					;
			}

			LocalBuilder retParams = emit.DeclareLocal(typeof(IDbDataParameter[]));

			emit
				.ldarg_0
				.ldloc    (locParams)
				.callvirt (_baseType, "PrepareParameters", _bindingFlags, typeof(object[]))
				.stloc    (retParams)
				;

			return retParams;
		}

		private void BuildParameter(ParameterInfo pi)
		{
			EmitHelper emit  = Context.MethodBuilder.Emitter;
			object[]   attrs = pi.GetCustomAttributes(typeof(ParamNameAttribute), true);

			string paramName = attrs.Length == 0?
				pi.Name: ((ParamNameAttribute)attrs[0]).Name;

			emit.ldloc        (_locManager);

			if (paramName[0] != '@')
			{
				emit
					.ldarg_0
					.ldloc    (_locManager)
					.ldstr    (paramName)
					.callvirt (_baseType, "GetSpParameterName", _bindingFlags, typeof(DbManager), typeof(string))
					;
			}
			else
				emit.ldstr    (paramName);

			Type   type       = pi.ParameterType;
			string methodName = "Parameter";

			if (pi.ParameterType.IsByRef)
			{
				if (_outputParameters == null)
					_outputParameters = new ArrayList();

				_outputParameters.Add(pi);

				type       = pi.ParameterType.GetElementType();
				methodName = pi.IsOut? "OutputParameter": "InputOutputParameter";
			}

			if (type.IsEnum)
			{
				emit
					.ldloc    (_locManager)
					.callvirt (typeof(DbManager).GetProperty("MappingSchema").GetGetMethod())
					;
			}

			emit.ldargEx      (pi, true);

			if (type.IsEnum)
				emit.callvirt (typeof(MappingSchema), "MapEnumToValue", typeof(object));

			emit.callvirt     (typeof(DbManager), methodName, typeof(string), typeof(object));
		}

		private LocalBuilder BuildParametersWithDiscoverParameters()
		{
			EmitHelper   emit      = Context.MethodBuilder.Emitter;
			LocalBuilder locParams = emit.DeclareLocal(typeof(object[]));

			emit
				.ldc_i4_            (_paramList.Count)
				.newarr             (typeof(object))
				.stloc              (locParams)
				;

			for (int i = 0; i < _paramList.Count; i++)
			{
				ParameterInfo pi = (ParameterInfo)_paramList[i];

				emit
					.ldloc          (locParams)
					.ldc_i4_        (i)
					.ldarg          (pi)
					.boxIfValueType (pi.ParameterType)
					.stelem_ref
					.end()
					;
			}

			return locParams;
		}

		private void GetOutRefParameters()
		{
			EmitHelper emit = Context.MethodBuilder.Emitter;

			if (_outputParameters != null)
			{
				LocalBuilder param = emit.DeclareLocal(typeof(IDataParameter));

				foreach (ParameterInfo pi in _outputParameters)
				{
					Type type = pi.ParameterType.GetElementType();

					emit
						.ldarg(pi)
						;

					// Get parameter.
					//
					object[] attrs = pi.GetCustomAttributes(typeof(ParamNameAttribute), true);

					string paramName = attrs.Length == 0 ?
						pi.Name : ((ParamNameAttribute)attrs[0]).Name;

					emit
						.ldloc (_locManager)
						;

					if (paramName[0] != '@')
					{
						emit
							.ldarg_0
							.ldloc                (_locManager)
							.ldstr                (paramName)
							.callvirt             (_baseType, "GetSpParameterName", _bindingFlags, typeof(DbManager), typeof(string))
							;
					}
					else
						emit.ldstr                (paramName);

					emit
						.callvirt                 (typeof(DbManager), "Parameter", typeof(string))
						.stloc                    (param)
						;

					// Process value.
					//
					if (type.IsEnum)
					{
							emit
								.ldloc            (_locManager)
								.callvirt         (typeof(DbManager).GetProperty("MappingSchema").GetGetMethod())
								.ldloc            (param)
								.callvirt         (typeof(IDataParameter).GetProperty("Value").GetGetMethod())
								.LoadType         (type)
								.callvirt         (typeof(MappingSchema), "MapValueToEnum", typeof(object), typeof(Type))
								.CastFromObject   (type)
								;
					}
					else
					{
						emit
							.ldarg_0
							.ldloc                (_locManager)
							.ldloc                (param)
							.callvirt             (typeof(IDataParameter).GetProperty("Value").GetGetMethod())
							;
						
						string converterName = GetConverterMethodName(type);

						if (converterName == null)
						{
							emit
								.LoadType         (type)
								.ldloc            (param)
								.callvirt         (typeof(DataAccessor), "ConvertChangeType", _bindingFlags, typeof(DbManager), typeof(object), typeof(Type), typeof(object))
								;
						}
						else
						{
							emit
								.ldloc            (param)
								.callvirt         (typeof(DataAccessor), converterName, _bindingFlags, typeof(DbManager), typeof(object), typeof(object))
								;
						}
					}

					if (type.IsValueType && type.IsPrimitive == false)
						emit.stobj(type);
					else
						emit.stind(type);
				}
			}

			foreach (MapOutputParametersValue v in _mapOutputParameters)
			{
				emit
					.ldloc     (_locManager)
					.ldstrEx   (v.ReturnValueMember)
					.ldarg     (v.ParameterInfo)
					.callvirt  (typeof(DbManager), "MapOutputParameters", typeof(string), typeof(object));
			}
		}

		private bool IsInterfaceOf(Type type, Type interfaceType)
		{
			Type[] types = type.GetInterfaces();

			foreach (Type t in types)
				if (t == interfaceType)
					return true;

			return type == interfaceType;
		}

		private string GetConverterMethodName(Type type)
		{
#if FW2
			Type underlyingType = Nullable.GetUnderlyingType(type);

			if (underlyingType != null)
			{
				if (underlyingType.IsEnum)
					underlyingType = Enum.GetUnderlyingType(underlyingType);

				if (underlyingType == typeof(Int16))    return "ConvertToNullableInt16";
				if (underlyingType == typeof(Int32))    return "ConvertToNullableInt32";
				if (underlyingType == typeof(Int64))    return "ConvertToNullableInt64";
				if (underlyingType == typeof(Byte))     return "ConvertToNullableByte";
				if (underlyingType == typeof(UInt16))   return "ConvertToNullableUInt16";
				if (underlyingType == typeof(UInt32))   return "ConvertToNullableUInt32";
				if (underlyingType == typeof(UInt64))   return "ConvertToNullableUInt64";
				if (underlyingType == typeof(Char))     return "ConvertToNullableChar";
				if (underlyingType == typeof(Double))   return "ConvertToNullableDouble";
				if (underlyingType == typeof(Single))   return "ConvertToNullableSingle";
				if (underlyingType == typeof(Boolean))  return "ConvertToNullableBoolean";
				if (underlyingType == typeof(DateTime)) return "ConvertToNullableDateTime";
				if (underlyingType == typeof(Decimal))  return "ConvertToNullableDecimal";
				if (underlyingType == typeof(Guid))     return "ConvertToNullableGuid";
			}
#endif

			if (type.IsPrimitive)
			{
				if (type.IsEnum)
					type = Enum.GetUnderlyingType(type);

				if (type == typeof(SByte))   return "ConvertToSByte";
				if (type == typeof(Int16))   return "ConvertToInt16";
				if (type == typeof(Int32))   return "ConvertToInt32";
				if (type == typeof(Int64))   return "ConvertToInt64";
				if (type == typeof(Byte))    return "ConvertToByte";
				if (type == typeof(UInt16))  return "ConvertToUInt16";
				if (type == typeof(UInt32))  return "ConvertToUInt32";
				if (type == typeof(UInt64))  return "ConvertToUInt64";
				if (type == typeof(Char))    return "ConvertToChar";
				if (type == typeof(Single))  return "ConvertToSingle";
				if (type == typeof(Double))  return "ConvertToDouble";
				if (type == typeof(Boolean)) return "ConvertToBoolean";
			}

			if (type == typeof(String))      return "ConvertToString";
			if (type == typeof(DateTime))    return "ConvertToDateTime";
			if (type == typeof(Decimal))     return "ConvertToDecimal";
			if (type == typeof(Guid))        return "ConvertToGuid";
			if (type == typeof(Stream))      return "ConvertToStream";
			if (type == typeof(XmlReader))   return "ConvertToXmlReader";
			if (type == typeof(Byte[]))      return "ConvertToByteArray";
			if (type == typeof(Char[]))      return "ConvertToCharArray";

			if (type == typeof(SqlByte))     return "ConvertToSqlByte";
			if (type == typeof(SqlInt16))    return "ConvertToSqlInt16";
			if (type == typeof(SqlInt32))    return "ConvertToSqlInt32";
			if (type == typeof(SqlInt64))    return "ConvertToSqlInt64";
			if (type == typeof(SqlSingle))   return "ConvertToSqlSingle";
			if (type == typeof(SqlBoolean))  return "ConvertToSqlBoolean";
			if (type == typeof(SqlDouble))   return "ConvertToSqlDouble";
			if (type == typeof(SqlDateTime)) return "ConvertToSqlDateTime";
			if (type == typeof(SqlDecimal))  return "ConvertToSqlDecimal";
			if (type == typeof(SqlMoney))    return "ConvertToSqlMoney";
			if (type == typeof(SqlGuid))     return "ConvertToSqlGuid";
			if (type == typeof(SqlString))   return "ConvertToSqlString";

			return null;
		}
	}
}