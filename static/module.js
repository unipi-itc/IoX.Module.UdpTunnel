((baseUrl, modElement) => {
$.getScript("https://cdnjs.cloudflare.com/ajax/libs/Chart.js/2.2.2/Chart.bundle.min.js").done(() => {
  var timeRange = 300000; // last timeRange milliseconds are shown in the graphs
  var sampleRate = 2000; // the statistics are collected every sampleRate ms
  var updateRate = 1000; // the graph is updated every updateRate ms

  var makeDataset = (label, color, map) => (data) => (
    {
      label: label,
      fill: false,
      lineTension: 0.0,
      backgroundColor: color,
      borderColor: color,
      borderCapStyle: 'round',
      borderDash: [],
      borderDashOffset: 0.0,
      borderJoinStyle: 'round',
      pointBorderColor: color,
      pointBackgroundColor: '#fff',
      pointBorderWidth: 1,
      pointHoverRadius: 5,
      pointHoverBackgroundColor: color,
      pointHoverBorderColor: 'rgba(220,220,220,1)',
      pointHoverBorderWidth: 2,
      pointRadius: 1,
      pointHitRadius: 10,
      data: map(data)
    }
  );

  var arrayDelta = f => array => {
    var r = [ NaN ];

    for (var i = 1; i < array.length; i++) {
      var prev = array[i-1];
      var curr = array[i];
      var delta = f(curr) - f(prev);
      var timediff = curr.ElapsedMS - prev.ElapsedMS;
      if (delta < 0 || timediff < 0)
        delta = NaN;
      else
        delta *= 1000 / timediff;
      r.push(delta);
    }

    return r;
  };

  var TimeScale = Chart.scaleService.getScaleConstructor('time')
  var RightTimeScale = TimeScale.extend({
    buildTicks: function() {
      TimeScale.prototype.buildTicks.call(this);
      this.ticks.shift(); // remove the first tick
    }
  });

  Chart.scaleService.registerScaleType(
    "rtime",
    RightTimeScale,
    Chart.scaleService.getScaleDefaults('time')
  );

  var ReactChart = React.createClass({
    render: function() {
      var redraw = ref => {
        if (ref == null)
          return;
        if (this.chart)
          this.chart.destroy();
        this.chart = new Chart(ref, {
          type: this.props.type,
          data: this.props.data,
          options: this.props.options
        });
      };
      return <canvas ref={redraw} height={this.props.height} width={this.props.width}/>;
    }
  });

  var Stats = React.createClass({
    getInitialState: function() {
      return {
        timestamps: [],
        data: []
      };
    },

    componentDidMount: function() {
      var collectData = Suave.EvReact.remoteCallback(
        baseUrl + 'stats',
        data => {
          var now = new Date()
          this.state.timestamps.push(now);
          this.state.data.push(data);

          var firstVisibleTime = now.getTime() - timeRange * 1.25;
          var i = 0;
          while (i < this.state.timestamps.length &&
            this.state.timestamps[i].getTime() < firstVisibleTime)
          {
            i++;
          }
          if (i != 0) {
            this.state.timestamps = this.state.timestamps.slice(i);
            this.state.data = this.state.data.slice(i);
          }

          // The scrollTimer takes care of notifying React about state changes
          // this.setState(this.state);
        }
      );
      var touchState = () => { this.setState(this.state); };
      this.dataTimer = setInterval(collectData, sampleRate);
      this.scrollTimer = setInterval(touchState, updateRate);
    },

    componentWillUnmount: function() {
      clearInterval(this.dataTimer);
      clearInterval(this.scrollTimer);
    },

    render: function() {
      var now = Date.now();
      var options = {
        animation: {
          duration: 0
        },
        scales: {
          reverse: true,
          xAxes: [{
              type: 'rtime',
              time: {
                unit: 'minute',
                max: now,
                min: now - timeRange,
              },
              gridLines: {
                color: 'rgba(0, 0, 0, 0.1)',
                zeroLineColor: 'rgba(0, 0, 0, 0.1)',
              },
              position: 'bottom'
          }],
          yAxes: [{
              type: 'linear',
              position: 'right'
          }]
        }
      };

      var data = {
        labels: this.state.timestamps,
        datasets: this.props.fields.map(d => d(this.state.data))
      };

      return <ReactChart type="line" data={data} options={options} width="600" height="250"/>
    }
  });

  var Configuration = React.createClass({
    getInitialState: function() {
      return {
        Verbose: false
      };
    },

    getConfig: function() {
      Suave.EvReact.remoteCallback(
        baseUrl + 'getConfig',
        data => this.setState(data)
      )();
    },

    setConfig: function() {
      Suave.EvReact.remoteCallback(
        baseUrl + 'saveConfig',
        data => this.getConfig(),
        true
      )(this.state);
    },

    reloadConfig: function() {
      Suave.EvReact.remoteCallback(
        baseUrl + 'reloadConfig',
        data => this.getConfig(),
        true
      )();
    },

    componentDidMount: function() {
      if (this.props.fields.indexOf("Encoding") < 0) {
        this.getConfig();
      } else {
        Suave.EvReact.remoteCallback(
          baseUrl + 'getEncodings',
          data => { this.encodings = data.sort(); this.getConfig(); }
        )();
      }
    },

    render: function() {
      var renderField = id => {
        if (!(id in this.state))
          return;

        var elem = null;
        if (id == "Encoding") {
          var makeOption = enc => {
            var enc = enc.toUpperCase();
            return (
              <option selected={this.state[id].toUpperCase() == enc} onChange={event => this.setState({ [id]: enc })}>
                {enc}
              </option>
            );
          };
          elem = <select className="form-control">{this.encodings.map(makeOption)}</select>;
        } else if (typeof(this.state[id]) == "boolean") {
          elem =
            <input
              className="form-control"
              type="checkbox"
              checked={this.state[id]}
              onChange={event => this.setState({ [id]: event.target.checked })} />;
        } else {
          elem =
            <input
              className="form-control"
              type="text"
              value={this.state[id]}
              onChange={event => this.setState({ [id]: event.target.value })} />;
        }

        return (
          <div key={id} className="form-group row">
            <label className="col-sm-3 control-label text-right">{id}</label>
            <div className="col-sm-9">
              {elem}
            </div>
          </div>
        );
      };

      return (
        <form className="form-horizontal">
          {this.props.fields.map(renderField)}
          <div className="row">
            <div className="col-sm-2" />
            <div className="col-sm-2">
              <input type="button" className="form-control" onClick={this.setConfig} value="Apply" />
            </div>
            <div className="col-sm-1" />
            <div className="col-sm-2">
              <input type="button" className="form-control" onClick={this.getConfig} value="Refresh" />
            </div>
            <div className="col-sm-1" />
            <div className="col-sm-2">
              <input type="button" className="form-control" onClick={this.reloadConfig} value="Reload" />
            </div>
            <div className="col-sm-2" />
          </div>
        </form>
      );
    }
  });

  var ConfStatModule = React.createClass({
    render: function() {
      return (
        <div>
          <ul className="nav nav-tabs" role="tablist">
            <li className="nav-item active">
              <a className="nav-link active" data-toggle="tab" href="#stats" role="tab">Statistics</a>
            </li>
            <li className="nav-item">
              <a className="nav-link" data-toggle="tab" href="#config" role="tab">Configuration</a>
            </li>
          </ul>
          <div className="tab-content">
            <div className="tab-pane active" id="stats" role="tabpanel"><Stats fields={this.props.statFields}/></div>
            <div className="tab-pane" id="config" role="tabpanel"><Configuration fields={this.props.configFields} /></div>
          </div>
        </div>
      );
    }
  });

  var DispatcherModule = React.createClass({
    render: function() {
      var configFields = [
        "DestinationHost",
        "DestinationPort",
        "ReadOnly",
        "Verbose"
      ];

      var statFields = [
        makeDataset("Incoming bytes/s", 'rgba(75,192,192,1)', arrayDelta(d => d.Total.Incoming.Bytes)),
        makeDataset("Outgoing bytes/s", 'rgba(192,75,75,1)', arrayDelta(d => d.Total.Outgoing.Bytes))
      ];

      return <ConfStatModule statFields={statFields} configFields={configFields} />;
    }
  });

  var CollectorModule = React.createClass({
    render: function() {
      var configFields = [
        "Destination",
        "Encoding",
        "StoreRegex",
        "UrgentRegex",
        "SpamRegex",
        "Port",
        "BufferSizeThreshold",
        "BufferTimeoutMS",
        "ReadOnly",
        "Verbose"
      ];

      var statFields = [
        makeDataset("Incoming bytes/s", 'rgba(75,192,192,1)', arrayDelta(d => d.Total.Incoming.Bytes)),
        makeDataset("Outgoing bytes/s", 'rgba(192,75,75,1)', arrayDelta(d => d.Total.Outgoing.Bytes)),
        makeDataset("Stored bytes/s", 'rgba(75,75,75,1)', arrayDelta(d => d.Store.Outgoing.Bytes))
      ];

      return <ConfStatModule statFields={statFields} configFields={configFields} />;
    }
  });

  var Module = React.createClass({
    getInitialState: function() {
      return {
        modType: ""
      };
    },

    componentDidMount: function() {
      // Find out the type of module from the stats it provides
      Suave.EvReact.remoteCallback(
        baseUrl + 'stats',
        data => this.setState({ modType: "Urgent" in data ? "collector" : "dispatcher" })
      )();
    },

    render: function() {
      if (this.state.modType == "dispatcher")
        return <DispatcherModule />;
      else if (this.state.modType == "collector")
        return <CollectorModule />;
      else
        return <div />;
    }
  });

  ReactDOM.render(<Module/>, modElement);
})
})
