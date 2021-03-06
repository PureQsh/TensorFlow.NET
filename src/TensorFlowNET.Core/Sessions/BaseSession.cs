﻿using NumSharp.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Tensorflow
{
    public class BaseSession
    {
        protected Graph _graph;
        protected bool _opened;
        protected bool _closed;
        protected int _current_version;
        protected byte[] _target;
        protected IntPtr _session;

        public BaseSession(string target = "", Graph graph = null)
        {
            if(graph is null)
            {
                _graph = ops.get_default_graph();
            }
            else
            {
                _graph = graph;
            }

            _target = UTF8Encoding.UTF8.GetBytes(target);
            var opts = c_api.TF_NewSessionOptions();
            var status = new Status();
            _session = c_api.TF_NewSession(_graph, opts, status);

            c_api.TF_DeleteSessionOptions(opts);
        }

        public virtual NDArray run(Tensor fetches, Dictionary<Tensor, NDArray> feed_dict = null)
        {
            return _run(fetches, feed_dict);
        }

        public virtual NDArray run(Operation fetches, Dictionary<Tensor, NDArray> feed_dict = null)
        {
            return _run(fetches, feed_dict);
        }

        private NDArray _run<T>(T fetches, Dictionary<Tensor, NDArray> feed_dict = null)
        {
            var feed_dict_tensor = new Dictionary<Tensor, NDArray>();

            if (feed_dict != null)
            {
                foreach (var feed in feed_dict)
                {
                    feed_dict_tensor[feed.Key] = feed.Value;
                }
            }

            // Create a fetch handler to take care of the structure of fetches.
            var fetch_handler = new _FetchHandler<T>(_graph, fetches, feed_dict_tensor);

            // Run request and get response.
            // We need to keep the returned movers alive for the following _do_run().
            // These movers are no longer needed when _do_run() completes, and
            // are deleted when `movers` goes out of scope when this _run() ends.
            var _ = _update_with_movers();
            var final_fetches = fetch_handler.fetches();
            var final_targets = fetch_handler.targets();

            // We only want to really perform the run if fetches or targets are provided,
            // or if the call is a partial run that specifies feeds.
            var results = _do_run(final_targets.Select(x => (Operation)(object)x).ToList(), final_fetches, feed_dict_tensor);

            return fetch_handler.build_results(this, results);
        }

        /// <summary>
        /// Runs a step based on the given fetches and feeds.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="target_list">A list of operations to be run, but not fetched.</param>
        /// <param name="fetch_list"></param>
        /// <param name="feed_dict"></param>
        /// <returns>
        /// A list of numpy ndarrays, corresponding to the elements of
        /// `fetch_list`.  If the ith element of `fetch_list` contains the
        /// name of an operation, the first Tensor output of that operation
        /// will be returned for that element.
        /// </returns>
        private NDArray[] _do_run(List<Operation> target_list, List<Tensor> fetch_list, Dictionary<Tensor, NDArray> feed_dict)
        {
            var feeds = feed_dict.Select(x => new KeyValuePair<TF_Output, Tensor>(x.Key._as_tf_output(), new Tensor(x.Value))).ToArray();
            var fetches = fetch_list.Select(x => x._as_tf_output()).ToArray();
            var targets = target_list;

            return _call_tf_sessionrun(feeds, fetches, target_list);
        }

        private unsafe NDArray[] _call_tf_sessionrun(KeyValuePair<TF_Output, Tensor>[] feed_dict, TF_Output[] fetch_list, List<Operation> target_list)
        {
            // Ensure any changes to the graph are reflected in the runtime.
            _extend_graph();

            var status = new Status();

            var output_values = fetch_list.Select(x => IntPtr.Zero).ToArray();

            c_api.TF_SessionRun(_session,
                run_options: null,
                inputs: feed_dict.Select(f => f.Key).ToArray(),
                input_values: feed_dict.Select(f => (IntPtr)f.Value).ToArray(),
                ninputs: feed_dict.Length,
                outputs: fetch_list,
                output_values: output_values,
                noutputs: fetch_list.Length,
                target_opers: target_list.Select(f => (IntPtr)f).ToArray(),
                ntargets: target_list.Count,
                run_metadata: IntPtr.Zero,
                status: status);

            status.Check(true);

            var result = new NDArray[fetch_list.Length];

            for (int i = 0; i < fetch_list.Length; i++)
            {
                result[i] = fetchValue(output_values[i]);
            }

            return result;
        }

        private unsafe NDArray fetchValue(IntPtr output)
        {
            var tensor = new Tensor(output);
            NDArray nd = null;
            Type type = tensor.dtype.as_numpy_datatype();
            var ndims = tensor.shape.Select(x => (int)x).ToArray();

            switch (tensor.dtype)
            {
                case TF_DataType.TF_STRING:
                    var bytes = tensor.Data();
                    // wired, don't know why we have to start from offset 9.
                    var str = UTF8Encoding.Default.GetString(bytes, 9, bytes.Length - 9);
                    nd = np.array(str).reshape();
                    break;
                case TF_DataType.TF_INT16:
                    var shorts = new short[tensor.size];
                    for (ulong i = 0; i < tensor.size; i++)
                        shorts[i] = *(short*)(c_api.TF_TensorData(output) + (int)(tensor.dataTypeSize * i));
                    nd = np.array(shorts).reshape(ndims);
                    break;
                case TF_DataType.TF_INT32:
                    var ints = new int[tensor.size];
                    for (ulong i = 0; i < tensor.size; i++)
                        ints[i] = *(int*)(c_api.TF_TensorData(output) + (int)(tensor.dataTypeSize * i));
                    nd = np.array(ints).reshape(ndims);
                    break;
                case TF_DataType.TF_FLOAT:
                    var floats = new float[tensor.size];
                    for (ulong i = 0; i < tensor.size; i++)
                        floats[i] = *(float*)(c_api.TF_TensorData(output) + (int)(tensor.dataTypeSize * i));
                    nd = np.array(floats).reshape(ndims);
                    break;
                case TF_DataType.TF_DOUBLE:
                    var doubles = new double[tensor.size];
                    for (ulong i = 0; i < tensor.size; i++)
                        doubles[i] = *(double*)(c_api.TF_TensorData(output) + (int)(tensor.dataTypeSize * i));
                    nd = np.array(doubles).reshape(ndims);
                    break;
                default:
                    throw new NotImplementedException("can't fetch output");
            }

            return nd;
        }

        /// <summary>
        /// If a tensor handle that is fed to a device incompatible placeholder, 
        /// we move the tensor to the right device, generate a new tensor handle, 
        /// and update feed_dict to use the new handle.
        /// </summary>
        private List<object> _update_with_movers()
        {
            return new List<object> { };
        }

        private void _extend_graph()
        {

        }
    }
}
