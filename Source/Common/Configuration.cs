using System;

using BLToolkit.EditableObjects;
using BLToolkit.Mapping;
using BLToolkit.TypeBuilder;

namespace BLToolkit.Common
{
	public static class Configuration
	{
		public enum NullEquivalent { DBNull, Null, Value }

		private static NullEquivalent _checkNullReturnIfNull = NullEquivalent.DBNull;
		/// <summary>
		/// Specifies what value should be returned by <c>TypeAccessor.CheckNull</c>
		/// if <see cref="BLToolkit.Reflection.IsNullHandler"/> was specified and interpreted current property 
		/// value as null. Default is: <see cref="DBNull"/>.
		/// </summary>
		public  static NullEquivalent  CheckNullReturnIfNull
		{
			get { return _checkNullReturnIfNull;  }
			set { _checkNullReturnIfNull = value; }
		}

		private static bool _trimOnMapping;
		/// <summary>
		/// Controls global trimming behaviour of mapper. Specifies whether trailing spaces
		/// should be trimmed when mapping from one entity to another. Default is: false. 
		/// To specify trimming behaviour other than global, please user <see cref="TrimmableAttribute"/>.
		/// </summary>
		public  static bool  TrimOnMapping
		{
			get { return _trimOnMapping;  }
			set { _trimOnMapping = value; }
		}

		private static bool _trimDictionaryKey = true;
		/// <summary>
		/// Controls global trimming behaviour of mapper for dictionary keys. Specifies whether trailing spaces
		/// should be trimmed when adding keys to dictionaries. Default is: true. 
		/// </summary>
		public  static bool  TrimDictionaryKey
		{
			get { return _trimDictionaryKey;  }
			set { _trimDictionaryKey = value; }
		}

		private static bool _notifyOnEqualSet = true;
		/// <summary>
		/// Specifies default behavior for PropertyChange generation. If set to true, <see cref="EditableObject.OnPropertyChanged"/>
		/// is invoked even when current value is same as new one. If set to false,  <see cref="EditableObject.OnPropertyChanged"/> 
		/// is invoked only when new value is being assigned. To specify notification behaviour other than default, please see 
		/// <see cref="PropertyChangedAttribute"/>
		/// </summary>
		public  static bool  NotifyOnEqualSet
		{
			get { return _notifyOnEqualSet;  }
			set { _notifyOnEqualSet = value; }
		}

		private static bool _filterOutBaseEqualAttributes = false;
		/// <summary>
		/// Controls whether attributes specified on base types should be always added to list of attributes
		/// when scanning hierarchy tree or they should be compared to attributes found on derived classes
		/// and added only when not present already. Default value: false;
		/// WARNING: setting this flag to "true" can significantly affect initial object generation/access performance
		/// use only when side effects are noticed with attribute being present on derived and base classes. 
		/// For builder attributes use provided attribute compatibility mechanism.
		/// </summary>
		public  static bool  FilterOutBaseEqualAttributes
		{
			get { return _filterOutBaseEqualAttributes; }
			set { _filterOutBaseEqualAttributes = value; }
		}
	}
}
